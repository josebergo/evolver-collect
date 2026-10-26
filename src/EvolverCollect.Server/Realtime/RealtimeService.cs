namespace EvolverCollect.Server.Realtime;

using EvolverCollect.Core.Abstractions;
using EvolverCollect.Core.Pipeline;
using EvolverCollect.Core.Runtime;
using Microsoft.AspNetCore.SignalR;

/// <summary>
/// 实时服务（单例）：
/// - 订阅 DataHub → 更新实时值缓存（RealtimeCache）；
/// - 500ms 节流聚合后广播：进程内事件（Blazor 控制台直接消费，零开销）+ SignalR（外部系统消费）；
/// - 通道状态变化即时广播。
/// </summary>
public sealed class RealtimeService : IDisposable
{
    private readonly RealtimeCache _cache;
    private readonly IHubContext<MonitorHub>? _hub;
    private readonly List<DataPoint> _buffer = [];
    private readonly object _bufferGate = new();
    private readonly Timer _flushTimer;

    /// <summary>进程内实时值批次事件（控制台组件订阅；回调在定时器线程，UI 需 InvokeAsync）。</summary>
    public event Action<IReadOnlyList<DataPoint>>? ValuesBatch;

    public event Action<ChannelStateEvent>? ChannelStateChanged;

    public RealtimeService(DataHub hub, RealtimeCache cache, ChannelManager channels, IHubContext<MonitorHub>? hubContext = null)
    {
        _cache = cache;
        _hub = hubContext;
        hub.Subscribe(OnPoints);
        channels.StateChanged += e =>
        {
            ChannelStateChanged?.Invoke(e);
            _ = SafeBroadcastAsync(nameof(MonitorHub.ChannelState), e);
        };
        _flushTimer = new Timer(_ => Flush(), null, 500, 500);
    }

    private void OnPoints(IReadOnlyList<DataPoint> points)
    {
        _cache.Update(points);
        if (ValuesBatch is null && _hub is null) return;
        lock (_bufferGate) _buffer.AddRange(points);
    }

    private void Flush()
    {
        IReadOnlyList<DataPoint> batch;
        lock (_bufferGate)
        {
            if (_buffer.Count == 0) return;
            batch = [.. _buffer];
            _buffer.Clear();
        }
        ValuesBatch?.Invoke(batch);
        _ = SafeBroadcastAsync(nameof(MonitorHub.Values), batch);
    }

    private async Task SafeBroadcastAsync(string method, object arg)
    {
        if (_hub is null) return;
        try { await _hub.Clients.All.SendAsync(method, arg); }
        catch { /* SignalR 推送失败不影响采集 */ }
    }

    public void Dispose() => _flushTimer.Dispose();
}
