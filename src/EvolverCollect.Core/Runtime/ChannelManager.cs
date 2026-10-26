namespace EvolverCollect.Core.Runtime;

using EvolverCollect.Core.Abstractions;
using EvolverCollect.Core.Domain;
using EvolverCollect.Core.Pipeline;
using Microsoft.Extensions.Logging;

/// <summary>
/// 通道运行时管理器：根据配置启动/停止各通道，聚合状态，转发写操作。
/// 由宿主注册为单例；配置变更后调用 RestartChannelAsync 热生效。
/// </summary>
public sealed class ChannelManager : IAsyncDisposable
{
    private readonly IChannelConfigSource _source;
    private readonly Dictionary<string, IDriverFactory> _factories;
    private readonly DataHub _hub;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly Dictionary<long, ChannelRuntime> _runtimes = [];
    private readonly object _gate = new();

    public ChannelManager(
        IChannelConfigSource source,
        IEnumerable<IDriverFactory> factories,
        DataHub hub,
        ILoggerFactory? loggerFactory = null)
    {
        _source = source;
        _factories = factories.ToDictionary(f => f.DriverId, StringComparer.OrdinalIgnoreCase);
        _hub = hub;
        _loggerFactory = loggerFactory;
    }

    public event Action<ChannelStateEvent>? StateChanged;

    /// <summary>启动所有 Enabled 通道（宿主启动时调用）。单通道失败不影响其余。</summary>
    public async Task StartAllAsync(CancellationToken ct = default)
    {
        var snapshots = await _source.LoadEnabledAsync(ct).ConfigureAwait(false);
        foreach (var snapshot in snapshots)
        {
            try { await StartChannelAsync(snapshot).ConfigureAwait(false); }
            catch (Exception) { /* 启动失败保留在配置中，界面可见 Degraded 原因 */ }
        }
    }

    public async Task StartChannelAsync(long channelId, CancellationToken ct = default)
    {
        var snapshot = await _source.LoadAsync(channelId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"通道 {channelId} 不存在");
        await StartChannelAsync(snapshot).ConfigureAwait(false);
    }

    private async Task StartChannelAsync(ChannelSnapshot snapshot)
    {
        ChannelRuntime runtime;
        lock (_gate)
        {
            if (_runtimes.TryGetValue(snapshot.Channel.Id, out var existing))
                return; // 已在运行
            if (!_factories.TryGetValue(snapshot.Channel.DriverId, out var factory))
                throw new InvalidOperationException($"未注册的驱动类型: {snapshot.Channel.DriverId}");
            var logger = _loggerFactory?.CreateLogger($"Channel[{snapshot.Channel.Name}]");
            runtime = new ChannelRuntime(snapshot, factory, _hub, logger);
            _runtimes[snapshot.Channel.Id] = runtime;
        }
        runtime.StateChanged += e => StateChanged?.Invoke(e);
        await runtime.StartAsync().ConfigureAwait(false);
    }

    public async Task StopChannelAsync(long channelId)
    {
        ChannelRuntime? runtime;
        lock (_gate)
        {
            if (!_runtimes.Remove(channelId, out runtime)) return;
        }
        if (runtime is not null) await runtime.StopAsync().ConfigureAwait(false);
    }

    /// <summary>配置变更后热重启通道（先停再启，数据管道不断）。</summary>
    public async Task RestartChannelAsync(long channelId, CancellationToken ct = default)
    {
        await StopChannelAsync(channelId).ConfigureAwait(false);
        await StartChannelAsync(channelId, ct).ConfigureAwait(false);
    }

    public async Task StopAllAsync()
    {
        List<ChannelRuntime> runtimes;
        lock (_gate)
        {
            runtimes = [.. _runtimes.Values];
            _runtimes.Clear();
        }
        foreach (var r in runtimes) await r.StopAsync().ConfigureAwait(false);
    }

    public async Task<WriteResult> WriteTagAsync(long channelId, TagDefinition tag, object value, string deviceName, int? stationNo, CancellationToken ct)
    {
        ChannelRuntime? runtime;
        lock (_gate) _runtimes.TryGetValue(channelId, out runtime);
        return runtime is null
            ? WriteResult.Fail("通道未运行")
            : await runtime.WriteTagAsync(tag, value, deviceName, stationNo, ct).ConfigureAwait(false);
    }

    public IReadOnlyList<ChannelStatus> GetStatusList()
    {
        lock (_gate) return [.. _runtimes.Values.Select(r => r.GetStatus())];
    }

    public ChannelStatus? GetStatus(long channelId)
    {
        lock (_gate) return _runtimes.TryGetValue(channelId, out var r) ? r.GetStatus() : null;
    }

    public bool IsRunning(long channelId)
    {
        lock (_gate) return _runtimes.ContainsKey(channelId);
    }

    public async ValueTask DisposeAsync() => await StopAllAsync().ConfigureAwait(false);
}
