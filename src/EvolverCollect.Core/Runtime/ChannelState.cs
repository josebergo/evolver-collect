namespace EvolverCollect.Core.Runtime;

/// <summary>通道运行状态机：Created → Starting → Running ⇄ Degraded(重连中) → Stopping → Stopped。</summary>
public enum ChannelState
{
    Created,
    Starting,
    Running,
    Degraded,
    Stopping,
    Stopped,
}

/// <summary>状态迁移事件（供控制台实时展示与 SignalR 推送）。</summary>
public sealed record ChannelStateEvent(long ChannelId, ChannelState State, string? Error = null);
