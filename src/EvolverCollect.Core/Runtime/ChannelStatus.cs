namespace EvolverCollect.Core.Runtime;

/// <summary>通道运行状态快照（API / 控制台展示用）。</summary>
public sealed record ChannelStatus(
    long ChannelId,
    string Name,
    string DriverId,
    ChannelState State,
    bool Connected,
    long TotalReads,
    long FailedReads,
    double SuccessRate,
    long TotalPoints,
    TimeSpan LastLatency,
    string? LastError,
    DateTime? LastErrorAt,
    int DeviceCount,
    int TagCount,
    DateTime StartedAt);
