namespace EvolverCollect.Drivers.Modbus;

/// <summary>Modbus 四种数据区。</summary>
public enum ModbusArea
{
    Coil = 1,            // 线圈（可读写开关量），功能码 01/05/15
    DiscreteInput = 2,   // 离散输入（只读开关量），功能码 02
    HoldingRegister = 3, // 保持寄存器（可读写），功能码 03/06/16
    InputRegister = 4,   // 输入寄存器（只读），功能码 04
}

/// <summary>
/// Modbus 地址解析。支持两种写法（详见文档中心《Modbus 从零》地址规则篇）：
/// - 经典 5 位：0xxxx 线圈 / 1xxxx 离散输入 / 3xxxx 输入寄存器 / 4xxxx 保持寄存器（1 基，如 40001 = HR 偏移 0）
/// - 前缀式：C100 / DI100 / IR100 / HR100（0 基，不受 1 万偏移限制）
/// </summary>
public sealed record ModbusAddress(ModbusArea Area, ushort Offset)
{
    public static ModbusAddress Parse(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new FormatException("地址为空");

        var s = address.Trim();
        var upper = s.ToUpperInvariant();

        // 前缀式（0 基）
        if (upper.StartsWith("HR")) return New(ModbusArea.HoldingRegister, s[2..], false);
        if (upper.StartsWith("IR")) return New(ModbusArea.InputRegister, s[2..], false);
        if (upper.StartsWith("DI")) return New(ModbusArea.DiscreteInput, s[2..], false);
        if (upper.StartsWith("C")) return New(ModbusArea.Coil, s[1..], false);

        // 经典式：首位 0/1/3/4 表示数据区，后 4 位为 1 基偏移（如 40001）
        if (!int.TryParse(s, out var n) || n < 0 || s.Length < 5)
            throw new FormatException($"无法识别的 Modbus 地址: {address}（支持 40001 式或 HR100/C1 式）");
        var areaDigit = s[0];
        var rest = s[1..];
        var offset = int.Parse(rest);
        if (offset < 1 || offset > 65536)
            throw new FormatException($"Modbus 地址偏移越界: {address}");
        return areaDigit switch
        {
            '0' => new ModbusAddress(ModbusArea.Coil, (ushort)(offset - 1)),
            '1' => new ModbusAddress(ModbusArea.DiscreteInput, (ushort)(offset - 1)),
            '3' => new ModbusAddress(ModbusArea.InputRegister, (ushort)(offset - 1)),
            '4' => new ModbusAddress(ModbusArea.HoldingRegister, (ushort)(offset - 1)),
            _ => throw new FormatException($"Modbus 地址首字符必须是 0/1/3/4: {address}"),
        };
    }

    public bool IsRegister => Area is ModbusArea.HoldingRegister or ModbusArea.InputRegister;

    public override string ToString() => Area switch
    {
        ModbusArea.Coil => $"C{Offset}",
        ModbusArea.DiscreteInput => $"DI{Offset}",
        ModbusArea.HoldingRegister => $"HR{Offset}",
        _ => $"IR{Offset}",
    };

    private static ModbusAddress New(ModbusArea area, string number, bool oneBased) =>
        int.TryParse(number, out var n) && n >= 0 && n <= 65535
            ? new ModbusAddress(area, (ushort)(oneBased ? n - 1 : n))
            : throw new FormatException($"地址偏移不合法: {number}");
}
