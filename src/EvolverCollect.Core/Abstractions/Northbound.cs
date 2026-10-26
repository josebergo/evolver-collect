namespace EvolverCollect.Core.Abstractions;

/// <summary>北向应用运行选项：点位过滤 + 报文格式 + 批量策略。</summary>
public sealed record NorthAppOptions(
    string? IncludeFilter,
    string? ExcludeFilter,
    string Format,          // json-lines | json-array | csv
    int BatchSize,
    int BatchIntervalMs)
{
    public static NorthAppOptions Default { get; } = new(null, null, "json-lines", 100, 500);
}

/// <summary>北向数据出口。实现约定：SendAsync 返回 false 表示当前不可发送（断网/未连上），
/// 由运行时把数据转存离线队列（断点续传），实现方不需要自己做重试。</summary>
public interface INorthboundSink : IAsyncDisposable
{
    string Type { get; }
    Task<ConnectResult> ConnectAsync(CancellationToken ct);
    Task DisconnectAsync();

    /// <summary>实时发送：points 供按设备路由等结构化需求使用；formattedPayload 为按配置格式化好的报文。</summary>
    Task<bool> SendAsync(IReadOnlyList<DataPoint> points, string formattedPayload, CancellationToken ct);

    /// <summary>回放离线积压（报文已格式化，原样发送）。</summary>
    Task<bool> SendRawAsync(string payload, CancellationToken ct);
}

public interface INorthboundFactory
{
    string Type { get; }
    NorthboundDescriptor Descriptor { get; }
    /// <summary>根据北向应用的连接参数 JSON 与运行选项创建出口实例。</summary>
    INorthboundSink Create(string connectionJson, NorthAppOptions options);
}

/// <summary>北向元数据（含参数 JSON Schema，控制台自动渲染表单，与驱动一致）。</summary>
public sealed record NorthboundDescriptor(
    string Type,
    string DisplayName,
    string ParameterSchemaJson,
    string? DocsSlug = null);
