namespace EvolverCollect.Core.Pipeline;

using EvolverCollect.Core.Abstractions;
using EvolverCollect.Core.Domain;
using Microsoft.Extensions.Logging;

/// <summary>
/// 北向应用管理器：根据配置启停各北向运行时，聚合状态，定期 FIFO 淘汰离线队列超限数据。
/// </summary>
public sealed class NorthAppManager : IAsyncDisposable
{
    private readonly IChannelConfigSource _source;
    private readonly Dictionary<string, INorthboundFactory> _factories;
    private readonly DataHub _hub;
    private readonly ISpoolStore _spool;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly long _spoolMaxRows;
    private readonly Dictionary<long, NorthAppRuntime> _runtimes = [];
    private readonly object _gate = new();

    public NorthAppManager(
        IChannelConfigSource source,
        IEnumerable<INorthboundFactory> factories,
        DataHub hub,
        ISpoolStore spool,
        long spoolMaxRows = 1_000_000,
        ILoggerFactory? loggerFactory = null)
    {
        _source = source;
        _factories = factories.ToDictionary(f => f.Type, StringComparer.OrdinalIgnoreCase);
        _hub = hub;
        _spool = spool;
        _spoolMaxRows = spoolMaxRows;
        _loggerFactory = loggerFactory;
    }

    public async Task StartAllAsync(CancellationToken ct = default)
    {
        var configs = await _source.LoadNorthAppsAsync(ct).ConfigureAwait(false);
        foreach (var config in configs.Where(c => c.Enabled))
        {
            try { await StartRuntimeAsync(config).ConfigureAwait(false); }
            catch (Exception) { /* 单个北向失败不影响整体启动 */ }
        }
        _ = Task.Run(() => TrimLoopAsync(default));
    }

    private async Task StartRuntimeAsync(NorthAppConfig config)
    {
        NorthAppRuntime runtime;
        lock (_gate)
        {
            if (_runtimes.ContainsKey(config.Id)) return;
            if (!_factories.TryGetValue(config.Type, out var factory))
                throw new InvalidOperationException($"未注册的北向类型: {config.Type}");
            runtime = new NorthAppRuntime(config, factory, _hub, _spool,
                _loggerFactory?.CreateLogger($"North[{config.Name}]"));
            _runtimes[config.Id] = runtime;
        }
        await runtime.StartAsync().ConfigureAwait(false);
    }

    public async Task StartAppAsync(long id, CancellationToken ct = default)
    {
        var configs = await _source.LoadNorthAppsAsync(ct).ConfigureAwait(false);
        var config = configs.FirstOrDefault(c => c.Id == id)
            ?? throw new InvalidOperationException($"北向应用 {id} 不存在");
        await StartRuntimeAsync(config).ConfigureAwait(false);
    }

    public async Task RestartAppAsync(long id, CancellationToken ct = default)
    {
        await StopAppAsync(id).ConfigureAwait(false);
        await StartAppAsync(id, ct).ConfigureAwait(false);
    }

    public async Task StopAppAsync(long id)
    {
        NorthAppRuntime? runtime;
        lock (_gate)
        {
            if (!_runtimes.Remove(id, out runtime)) return;
        }
        if (runtime is not null) await runtime.StopAsync().ConfigureAwait(false);
    }

    public async Task StopAllAsync()
    {
        List<NorthAppRuntime> runtimes;
        lock (_gate)
        {
            runtimes = [.. _runtimes.Values];
            _runtimes.Clear();
        }
        foreach (var r in runtimes) await r.StopAsync().ConfigureAwait(false);
    }

    public IReadOnlyList<NorthAppStatus> GetStatusList()
    {
        lock (_gate) return [.. _runtimes.Values.Select(r => r.GetStatus())];
    }

    private async Task TrimLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                List<long> ids;
                lock (_gate) ids = [.. _runtimes.Keys];
                foreach (var id in ids)
                    await _spool.TrimAsync(id, _spoolMaxRows, ct).ConfigureAwait(false);
            }
            catch { /* 淘汰失败下轮重试 */ }
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }

    public async ValueTask DisposeAsync() => await StopAllAsync().ConfigureAwait(false);
}
