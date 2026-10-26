namespace EvolverCollect.Server.Realtime;

using Microsoft.AspNetCore.SignalR;

/// <summary>
/// 实时监控 Hub（外部系统集成入口）。事件流：
/// - values：实时值批次（500ms 节流聚合）
/// - channelState：通道状态迁移
/// - alarms：报警事件（v0.3 接入）
/// </summary>
public sealed class MonitorHub : Hub
{
    public const string Values = "values";
    public const string ChannelState = "channelState";
    public const string Alarms = "alarms";
}
