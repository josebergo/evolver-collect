namespace EvolverCollect.Core.Domain;

using EvolverCollect.Core.Abstractions;

/// <summary>运行时所需的配置快照（Core 不依赖持久化框架，由 Storage 映射提供）。</summary>
public sealed record ChannelConfig(
    long Id,
    string Name,
    string DriverId,
    bool Enabled,
    string ConnectionJson,
    int DefaultPollIntervalMs,
    int TimeoutMs,
    int RetryCount,
    string? Description);

public sealed record DeviceConfig(
    long Id,
    long ChannelId,
    string Name,
    int? StationNo,
    long? TemplateId,
    bool Enabled,
    string? Description);

public sealed record NorthAppConfig(
    long Id,
    string Name,
    string Type,
    bool Enabled,
    string ConnectionJson,
    NorthAppOptions Options);

public sealed record AlarmRuleConfig(
    long Id,
    string Name,
    long TagId,
    string Condition,      // Gt | Lt | Eq | Ne | Change
    string Threshold,
    int Level,             // 1 提示 2 警告 3 严重
    int DurationEvaluations,
    int RecoverEvaluations,
    bool Enabled);

public sealed record TemplateInfo(
    long Id,
    string Vendor,
    string Model,
    string? Description,
    string TagsJson);

/// <summary>设备模板中的一个点位定义（模板应用到设备时转换为 TagEntity）。</summary>
public sealed record TemplateTag(
    string Name,
    string Address,
    string DataType,
    bool Writable = false,
    int? PollIntervalMs = null,
    double ScaleA = 1.0,
    double ScaleB = 0.0,
    double? Deadband = null,
    int? BitIndex = null,
    string? Description = null);

/// <summary>一个通道的完整启动快照：通道 + 启用的设备 + 全部点位。</summary>
public sealed record ChannelSnapshot(
    ChannelConfig Channel,
    IReadOnlyList<DeviceConfig> Devices,
    IReadOnlyList<TagDefinition> Tags);

/// <summary>Core 定义的配置数据源接口（由 Storage 实现，保持依赖方向 Storage → Core）。</summary>
public interface IChannelConfigSource
{
    Task<IReadOnlyList<ChannelSnapshot>> LoadEnabledAsync(CancellationToken ct = default);
    Task<ChannelSnapshot?> LoadAsync(long channelId, CancellationToken ct = default);
    Task<IReadOnlyList<NorthAppConfig>> LoadNorthAppsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AlarmRuleConfig>> LoadAlarmRulesAsync(CancellationToken ct = default);
}
