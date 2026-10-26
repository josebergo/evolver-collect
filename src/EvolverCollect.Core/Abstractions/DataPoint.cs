namespace EvolverCollect.Core.Abstractions;

/// <summary>数据质量戳：北向与界面据此判断数据可不可信。</summary>
public enum Quality
{
    Good = 0,
    Uncertain = 1,
    Bad = 2,
}

/// <summary>点位数据类型（跨协议统一；各驱动负责把协议原生类型映射到这里）。</summary>
public enum TagDataType
{
    Bool,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float,
    Double,
    String,
}

/// <summary>
/// 统一数据点：所有驱动输出、所有北向输入的同构格式。
/// 这是整个系统的“通用货币”——处理层与北向完全不感知协议差异。
/// </summary>
public sealed record DataPoint(
    long ChannelId,
    long DeviceId,
    long TagId,
    string DeviceName,
    string TagName,
    object? Value,
    Quality Quality,
    long TimestampMs,
    long? SourceTimestampMs = null)
{
    /// <summary>生成整批 Bad 点位（设备断连/读取失败时，仍带戳上报，让消费方知道数据不可信）。</summary>
    public static DataPoint BadPoint(
        long channelId, long deviceId, long tagId,
        string deviceName, string tagName, string reason, long timestampMs)
        => new(channelId, deviceId, tagId, deviceName, tagName, reason, Quality.Bad, timestampMs);

    public override string ToString() => $"{DeviceName}.{TagName}={Value}({Quality})@{TimestampMs}";
}
