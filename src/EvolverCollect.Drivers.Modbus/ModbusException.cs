namespace EvolverCollect.Drivers.Modbus;

/// <summary>
/// Modbus 异常响应（从站返回 功能码|0x80 + 异常码）。翻译成人话，方便现场排查与教学。
/// </summary>
public sealed class ModbusException(string message, byte functionCode, byte exceptionCode)
    : Exception(message)
{
    /// <summary>异常响应中的功能码（已置 0x80 位）。</summary>
    public byte FunctionCode { get; } = functionCode;

    /// <summary>从站返回的异常码。</summary>
    public byte ExceptionCode { get; } = exceptionCode;

    public static ModbusException Create(byte functionCode, byte exceptionCode) =>
        new(Describe(functionCode, exceptionCode), functionCode, exceptionCode);

    private static string Describe(byte fc, byte code) => code switch
    {
        1 => $"从站返回 01 非法功能码（FC{fc & 0x7F}）：该设备不支持此操作",
        2 => $"从站返回 02 非法数据地址（FC{fc & 0x7F}）：点位地址不存在，请核对数据区与偏移",
        3 => $"从站返回 03 非法数据值（FC{fc & 0x7F}）：数量/值超出设备允许范围",
        4 => $"从站返回 04 从站设备故障（FC{fc & 0x7F}）",
        5 => $"从站返回 05 确认（从站忙，稍后重试）",
        6 => $"从站返回 06 从站忙（FC{fc & 0x7F}）",
        8 => $"从站返回 08 存储奇偶校验错误",
        10 => $"从站返回 0A 网关路径不可用",
        11 => $"从站返回 0B 网关目标设备无响应",
        _ => $"从站返回异常码 {code:X2}（FC{fc & 0x7F}）",
    };
}
