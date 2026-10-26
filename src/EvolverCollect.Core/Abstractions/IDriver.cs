namespace EvolverCollect.Core.Abstractions;

/// <summary>
/// 南向驱动接口——所有协议驱动的统一契约。
/// 实现要求：
/// 1. 连接失败/读取失败返回结果对象而不是抛异常；
/// 2. 部分点位失败时，失败点位以 <see cref="Quality.Bad"/> 返回，成功点位正常返回；
/// 3. 驱动内部可自由做合并读优化（调用方按设备批量给点）。
/// </summary>
public interface IDriver : IAsyncDisposable
{
    DriverDescriptor Descriptor { get; }

    /// <summary>建立连接并完成协议握手（含首次重试不建议，重连由运行时统一负责）。</summary>
    Task<ConnectResult> ConnectAsync(CancellationToken ct);

    Task DisconnectAsync();

    /// <summary>读取一批点位，返回的点与 <paramref name="batch"/> 中的 Tag 一一对应（失败的以 Bad 返回）。</summary>
    Task<IReadOnlyList<DataPoint>> ReadAsync(DeviceBatch batch, CancellationToken ct);

    /// <summary>写单个点位（下发设定值/开关量）。stationNo 为该点所属设备的从站号/节点地址。</summary>
    Task<WriteResult> WriteAsync(TagDefinition tag, object value, string deviceName, int? stationNo, CancellationToken ct);

    /// <summary>支持地址浏览的驱动（如 OPC UA）返回浏览器，其余返回 null。</summary>
    ITagBrowser? CreateTagBrowser();

    DriverStats Stats { get; }
}

/// <summary>驱动工厂：根据通道的连接参数 JSON 创建驱动实例（每个通道一个驱动实例）。</summary>
public interface IDriverFactory
{
    string DriverId { get; }
    DriverDescriptor Descriptor { get; }
    IDriver Create(string connectionJson);
}
