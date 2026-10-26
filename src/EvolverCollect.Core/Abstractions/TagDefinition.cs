namespace EvolverCollect.Core.Abstractions;

/// <summary>点位定义（运行时形态，由配置存储映射而来）。</summary>
public sealed record TagDefinition(
    long Id,
    long DeviceId,
    string Name,
    string Address,
    TagDataType DataType,
    bool Writable,
    int? PollIntervalMs,
    double ScaleA,
    double ScaleB,
    double? Deadband,
    int? BitIndex,
    string? Description);

/// <summary>一次读取任务中同一设备的点位集合（调度器按设备分组后交给驱动）。</summary>
public sealed record DeviceBatch(
    long ChannelId,
    long DeviceId,
    string DeviceName,
    int? StationNo,
    IReadOnlyList<TagDefinition> Tags);

/// <summary>写点位请求。</summary>
public sealed record TagWriteRequest(TagDefinition Tag, object Value, string DeviceName, long ChannelId);
