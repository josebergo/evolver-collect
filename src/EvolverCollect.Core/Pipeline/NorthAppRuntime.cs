namespace EvolverCollect.Core.Pipeline;

using System.Threading.Channels;
using EvolverCollect.Core.Abstractions;
using EvolverCollect.Core.Domain;
using Microsoft.Extensions.Logging;

/// <summary>北向应用状态快照（界面展示）。</summary>
public sealed record NorthAppStatus(
    long Id,
    string Name,
    string Type,
    bool Enabled,
    bool Connected,
    long SentPoints,
    long SpooledPoints,
    long ReplayedPoints,
    long SpoolDepth,
    string? LastError);

/// <summary>
/// 单个北向应用的运行时：
/// - 订阅 DataHub，按过滤器筛点 → 有界队列 → 按 BatchSize/BatchIntervalMs 凑批 → 发送；
/// - 发送失败/断网：整批格式化后落离线队列（断点续传），后台重连；
/// - 重连成功后按 Id 顺序回放积压（水位推进，失败从断点继续）。
/// </summary>
public sealed class NorthAppRuntime : IAsyncDisposable
{
    private readonly NorthAppConfig _config;
    private readonly INorthboundFactory _factory;
    private readonly DataHub _hub;
    private readonly ISpoolStore _spool;
    private readonly ILogger? _logger;
    private readonly TagFilter _filter;
    private readonly Channel<DataPoint> _queue;
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    private INorthboundSink? _sink;
    private CancellationTokenSource? _cts;
    private IDisposable? _subscription;
    private Task? _consumeLoop;
    private Task? _replayLoop;
    private volatile bool _connected;
    private string? _lastError;
    private long _sentPoints, _spooledPoints, _replayedPoints;
    private long _spoolDepth;

    public NorthAppRuntime(
        NorthAppConfig config, INorthboundFactory factory, DataHub hub, ISpoolStore spool, ILogger? logger = null)
    {
        _config = config;
        _factory = factory;
        _hub = hub;
        _spool = spool;
        _logger = logger;
        _filter = new TagFilter(config.Options.IncludeFilter, config.Options.ExcludeFilter);
        _queue = Channel.CreateBounded<DataPoint>(new BoundedChannelOptions(20_000)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropNewest });
    }

    public long Id => _config.Id;

    /// <summary>重连间隔（默认 5s；测试时可调小）。</summary>
    public int ReconnectDelayMs { get; set; } = 5000;
    /// <summary>回放轮询间隔（默认 2s；测试时可调小）。</summary>
    public int ReplayPollMs { get; set; } = 2000;

    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts = new CancellationTokenSource();
        _sink = _factory.Create(_config.ConnectionJson, _config.Options);
        var result = await _sink.ConnectAsync(ct).ConfigureAwait(false);
        _connected = result.Success;
        _lastError = result.Error;
        _subscription = _hub.Subscribe(OnPoints);
        _consumeLoop = Task.Run(() => ConsumeLoopAsync(_cts.Token));
        _replayLoop = Task.Run(() => ReplayLoopAsync(_cts.Token));
        _ = Task.Run(() => RefreshDepthAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _subscription?.Dispose();
        var sink = _sink;
        if (sink is not null)
        {
            try { await sink.DisconnectAsync().ConfigureAwait(false); } catch { }
            try { await sink.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        try
        {
            await Task.WhenAll(_consumeLoop ?? Task.CompletedTask, _replayLoop ?? Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch { /* 收尾超时不阻塞 */ }
        _sink = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    public NorthAppStatus GetStatus() => new(
        Id, _config.Name, _config.Type, _config.Enabled, _connected,
        Interlocked.Read(ref _sentPoints), Interlocked.Read(ref _spooledPoints),
        Interlocked.Read(ref _replayedPoints), _spoolDepth, _lastError);

    // ---------- 内部实现 ----------

    private void OnPoints(IReadOnlyList<DataPoint> points)
    {
        var writer = _queue.Writer;
        foreach (var p in points)
        {
            if (!_filter.Matches(p)) continue;
            if (!writer.TryWrite(p))
            {
                // 队列满（北向持续消费不动）→ 立即落离线队列，fire-and-forget 由 SQLite 吸收
                _ = SpoolAsync([p]);
            }
        }
    }

    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        var reader = _queue.Reader;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var first = await reader.ReadAsync(ct).ConfigureAwait(false);
                var batch = new List<DataPoint>(_config.Options.BatchSize) { first };

                // 时间窗内凑批：达到 BatchSize 或 BatchIntervalMs 到期即发送
                using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
                window.CancelAfter(Math.Max(10, _config.Options.BatchIntervalMs));
                try
                {
                    while (batch.Count < _config.Options.BatchSize)
                    {
                        if (!await reader.WaitToReadAsync(window.Token).ConfigureAwait(false)) break;
                        while (batch.Count < _config.Options.BatchSize && reader.TryRead(out var p)) batch.Add(p);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* 凑批窗口到期 */ }

                await SendBatchAsync(batch, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* 正常停机 */ }
    }

    private async Task SendBatchAsync(List<DataPoint> batch, CancellationToken ct)
    {
        var payload = NorthFormat.FormatBatch(batch, _config.Options.Format);
        var sink = _sink;
        if (sink is null) return;

        bool ok;
        try { ok = _connected && await sink.SendAsync(batch, payload, ct).ConfigureAwait(false); }
        catch (Exception ex) { _lastError = ex.Message; ok = false; }

        if (ok)
        {
            Interlocked.Add(ref _sentPoints, batch.Count);
            _lastError = null;
            return;
        }

        _connected = false;
        _lastError ??= "发送失败";
        _logger?.LogWarning("北向 {Name} 发送失败，批次转入离线队列（{Count} 点）", _config.Name, batch.Count);
        await SpoolAsync(batch).ConfigureAwait(false);
        _ = TryReconnectAsync(ct);
    }

    private async Task SpoolAsync(IReadOnlyList<DataPoint> batch)
    {
        try
        {
            await _spool.EnqueueAsync(_config.Id, NorthFormat.FormatBatch(batch, _config.Options.Format)).ConfigureAwait(false);
            Interlocked.Add(ref _spooledPoints, batch.Count);
            _spoolDepth = await _spool.GetDepthAsync(_config.Id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "离线队列入队失败（磁盘满？），{Count} 个点将丢失", batch.Count);
        }
    }

    private async Task TryReconnectAsync(CancellationToken ct)
    {
        var sink = _sink;
        if (sink is null || _connected) return;
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connected || ct.IsCancellationRequested) return;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(50, ReconnectDelayMs)), ct).ConfigureAwait(false);
            await sink.DisconnectAsync().ConfigureAwait(false);
            var result = await sink.ConnectAsync(ct).ConfigureAwait(false);
            if (result.Success)
            {
                _connected = true;
                _lastError = null;
                _logger?.LogInformation("北向 {Name} 重连成功，开始回放积压", _config.Name);
            }
            else _lastError = result.Error;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _lastError = ex.Message; }
        finally { _connectGate.Release(); }
    }

    private async Task ReplayLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!_connected)
                {
                    await Task.Delay(Math.Max(50, ReplayPollMs), ct).ConfigureAwait(false);
                    continue;
                }
                var items = await _spool.DequeueAsync(_config.Id, 50, ct).ConfigureAwait(false);
                if (items.Count == 0)
                {
                    _spoolDepth = 0;
                    await Task.Delay(Math.Max(50, ReplayPollMs), ct).ConfigureAwait(false);
                    continue;
                }

                var sink = _sink;
                if (sink is null) return;
                var lastSentId = -1L;
                foreach (var item in items)
                {
                    if (await sink.SendRawAsync(item.Payload, ct).ConfigureAwait(false))
                    {
                        lastSentId = item.Id;
                        Interlocked.Increment(ref _replayedPoints);
                    }
                    else break;
                }
                if (lastSentId > 0)
                {
                    await _spool.MarkSentAsync(_config.Id, lastSentId, ct).ConfigureAwait(false);
                    _spoolDepth = await _spool.GetDepthAsync(_config.Id).ConfigureAwait(false);
                }
                else
                {
                    _connected = false; // 回放失败视同链路断，走重连
                    _ = TryReconnectAsync(ct);
                }
            }
        }
        catch (OperationCanceledException) { /* 正常停机 */ }
    }

    private async Task RefreshDepthAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _spoolDepth = await _spool.GetDepthAsync(_config.Id, ct).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }
}
