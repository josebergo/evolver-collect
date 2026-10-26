namespace EvolverCollect.Core.Runtime;

using System.Diagnostics;
using EvolverCollect.Core.Abstractions;
using EvolverCollect.Core.Domain;
using EvolverCollect.Core.Engine;
using EvolverCollect.Core.Pipeline;
using Microsoft.Extensions.Logging;

/// <summary>
/// 单个通道的采集运行时：
/// - 按采集周期分桶（桶内再按设备分组），每桶一个 PeriodicTimer 循环；
/// - 读取失败 → 断链：整批点位置为 Bad 发布一次（质量变化必须让北向感知）→ 指数退避重连；
/// - 全部异常在运行时内消化，绝不让异常逃逸终止轮询循环。
/// </summary>
public sealed class ChannelRuntime : IAsyncDisposable
{
    private readonly ChannelSnapshot _snapshot;
    private readonly IDriverFactory _factory;
    private readonly DataHub _hub;
    private readonly ILogger? _logger;
    private readonly TagEngine _engine = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly List<Task> _tasks = [];
    private readonly List<DeviceBatch> _allBatches = [];

    private IDriver? _driver;
    private CancellationTokenSource? _cts;
    private volatile bool _connected;
    private volatile bool _needReconnect;
    private int _reconnectAttempt;
    private DateTime _startedAt;

    public ChannelRuntime(ChannelSnapshot snapshot, IDriverFactory factory, DataHub hub, ILogger? logger)
    {
        _snapshot = snapshot;
        _factory = factory;
        _hub = hub;
        _logger = logger;
        _allBatches = BuildBuckets().SelectMany(b => b.Batches).ToList();
    }

    public long Id => _snapshot.Channel.Id;
    public string Name => _snapshot.Channel.Name;
    public ChannelState State { get; private set; } = ChannelState.Created;
    public bool Connected => _connected;
    public string? LastError { get; private set; }
    public DateTime? LastErrorAt { get; private set; }
    public int DeviceCount => _snapshot.Devices.Count(d => d.Enabled);
    public int TagCount => _allBatches.Sum(b => b.Tags.Count);

    public event Action<ChannelStateEvent>? StateChanged;

    public async Task StartAsync()
    {
        lock (_stateGate)
        {
            if (State is not (ChannelState.Created or ChannelState.Stopped)) return;
            State = ChannelState.Starting;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _startedAt = DateTime.Now;
        _driver = _factory.Create(_snapshot.Channel.ConnectionJson);

        var ok = await TryConnectAsync(ct).ConfigureAwait(false);
        _needReconnect = !ok;

        foreach (var bucket in BuildBuckets())
            _tasks.Add(Task.Run(() => RunBucketAsync(bucket, ct), ct));
        _tasks.Add(Task.Run(() => RunSupervisorAsync(ct), ct));

        SetState(ok ? ChannelState.Running : ChannelState.Degraded, ok ? null : LastError);
    }

    public async Task StopAsync()
    {
        lock (_stateGate)
        {
            if (State is ChannelState.Stopping or ChannelState.Stopped) return;
            State = ChannelState.Stopping;
        }

        _cts?.Cancel();
        try
        {
            if (_tasks.Count > 0)
                await Task.WhenAll(_tasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch { /* 轮询循环收尾超时不阻塞停机 */ }

        var driver = _driver;
        if (driver is not null)
        {
            try { await driver.DisconnectAsync().ConfigureAwait(false); } catch { }
            try { await driver.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _driver = null;
        _tasks.Clear();
        _connected = false;
        SetState(ChannelState.Stopped);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>写点位（控制台/API 下发设定值）。透传给驱动并计入统计。</summary>
    public async Task<WriteResult> WriteTagAsync(TagDefinition tag, object value, string deviceName, int? stationNo, CancellationToken ct)
    {
        var driver = _driver;
        if (driver is null || !_connected) return WriteResult.Fail("通道未连接");
        WriteResult result;
        try { result = await driver.WriteAsync(tag, value, deviceName, stationNo, ct).ConfigureAwait(false); }
        catch (Exception ex) { result = WriteResult.Fail(ex.Message); }
        driver.Stats.RecordWrite(result.Success, result.Error);
        return result;
    }

    public ChannelStatus GetStatus()
    {
        var stats = _driver?.Stats;
        return new ChannelStatus(
            Id, Name, _snapshot.Channel.DriverId, State, _connected,
            stats?.TotalReads ?? 0, stats?.FailedReads ?? 0,
            stats?.SuccessRate ?? 1.0, stats?.TotalPoints ?? 0,
            stats?.LastLatency ?? TimeSpan.Zero,
            LastError, LastErrorAt, DeviceCount, TagCount, _startedAt);
    }

    // ---------- 内部实现 ----------

    private async Task RunBucketAsync(Bucket bucket, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(bucket.IntervalMs));
            while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!_connected) continue; // 断链期间由 supervisor 负责重连与 Bad 通告
                foreach (var batch in bucket.Batches)
                    await ReadOnceAsync(batch, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* 正常停机 */ }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "通道 {Channel} 轮询循环异常退出", Name);
            OnLinkDown($"轮询循环异常: {ex.Message}");
        }
    }

    private async Task ReadOnceAsync(DeviceBatch batch, CancellationToken ct)
    {
        var driver = _driver;
        if (driver is null) return;

        var sw = Stopwatch.StartNew();
        try
        {
            var raw = await driver.ReadAsync(batch, ct).ConfigureAwait(false);
            sw.Stop();
            driver.Stats.RecordRead(sw.Elapsed, success: true, raw.Count);
            var processed = _engine.Process(batch, raw);
            if (processed.Count > 0) _hub.Publish(processed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            var reason = ex.InnerException?.Message ?? ex.Message;
            driver.Stats.RecordRead(sw.Elapsed, success: false, 0, reason);
            OnLinkDown(reason);
        }
    }

    private async Task RunSupervisorAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_needReconnect)
                {
                    var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, _reconnectAttempt)));
                    try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }

                    if (await TryConnectAsync(ct).ConfigureAwait(false))
                    {
                        _needReconnect = false;
                        _reconnectAttempt = 0;
                        _logger?.LogInformation("通道 {Channel} 重连成功", Name);
                        SetState(ChannelState.Running);
                    }
                    else
                    {
                        _reconnectAttempt++;
                    }
                }
                else
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { /* 正常停机 */ }
    }

    private async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        var driver = _driver;
        if (driver is null) return false;
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sw = Stopwatch.StartNew();
            var result = await driver.ConnectAsync(ct).ConfigureAwait(false);
            sw.Stop();
            if (result.Success)
            {
                _connected = true;
                LastError = null;
                _logger?.LogInformation("通道 {Channel} 连接成功（{Elapsed}ms）", Name, sw.ElapsedMilliseconds);
                return true;
            }
            LastError = result.Error;
            LastErrorAt = DateTime.Now;
            _logger?.LogWarning("通道 {Channel} 连接失败: {Error}", Name, result.Error);
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            LastErrorAt = DateTime.Now;
            return false;
        }
        finally { _connectGate.Release(); }
    }

    /// <summary>链路掉线：状态置 Degraded、触发重连、把该通道全部点位以 Bad 通告一次。</summary>
    private void OnLinkDown(string reason)
    {
        if (_connected)
        {
            _connected = false;
            SetState(ChannelState.Degraded, reason);
            _logger?.LogWarning("通道 {Channel} 链路中断: {Reason}", Name, reason);
            PublishAllBad(reason);
        }
        _needReconnect = true;
        LastError = reason;
        LastErrorAt = DateTime.Now;
    }

    private void PublishAllBad(string reason)
    {
        var ts = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var points = new List<DataPoint>(_allBatches.Sum(b => b.Tags.Count));
        foreach (var batch in _allBatches)
            foreach (var tag in batch.Tags)
                points.Add(DataPoint.BadPoint(batch.ChannelId, batch.DeviceId, tag.Id,
                    batch.DeviceName, tag.Name, reason, ts));
        _hub.Publish(points);
    }

    private void SetState(ChannelState state, string? error = null)
    {
        State = state;
        try { StateChanged?.Invoke(new ChannelStateEvent(Id, state, error ?? LastError)); }
        catch { /* 事件订阅者异常不影响状态机 */ }
    }

    private List<Bucket> BuildBuckets()
    {
        var defaultInterval = Math.Max(100, _snapshot.Channel.DefaultPollIntervalMs);
        var devices = _snapshot.Devices.Where(d => d.Enabled).ToDictionary(d => d.Id);
        return
        [
            .. _snapshot.Tags
                .Where(t => devices.ContainsKey(t.DeviceId))
                .GroupBy(t => Math.Max(100, t.PollIntervalMs ?? defaultInterval))
                .Select(g => new Bucket(
                    g.Key,
                    [.. g.GroupBy(t => t.DeviceId)
                        .Select(dg => new DeviceBatch(_snapshot.Channel.Id, dg.Key, devices[dg.Key].Name, devices[dg.Key].StationNo, [.. dg]))])),
        ];
    }

    private sealed record Bucket(int IntervalMs, List<DeviceBatch> Batches);
}
