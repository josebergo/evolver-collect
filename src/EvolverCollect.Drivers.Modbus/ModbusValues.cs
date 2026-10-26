namespace EvolverCollect.Drivers.Modbus;

using EvolverCollect.Core.Abstractions;

/// <summary>
/// 寄存器/线圈原始数据 ↔ 统一数据类型 的转换。
/// Modbus 寄存器为 16 位大端；32/64 位值按字序（big=ABCD 标准序 / little=CDAB 字交换序）组合。
/// 现场遇“浮点值差 65536 倍/乱码”多半是字序问题——可在通道参数里切 wordOrder。
/// </summary>
public static class ModbusValues
{
    public const int MaxReadRegisters = 125;   // FC03/04 单次最多 125 个寄存器
    public const int MaxReadCoils = 2000;      // FC01/02 单次最多 2000 个位
    private const int MaxWriteRegisters = 123; // FC16 单次最多 123 个寄存器

    /// <summary>点位占用的寄存器/线圈数量（String 类型 Modbus 暂不支持）。</summary>
    public static int ValueCount(TagDataType type) => type switch
    {
        TagDataType.Bool or TagDataType.Int16 or TagDataType.UInt16 => 1,
        TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float => 2,
        TagDataType.Int64 or TagDataType.UInt64 or TagDataType.Double => 4,
        _ => throw new NotSupportedException($"Modbus 不支持数据类型 {type}（建议 Int16/UInt16/Int32/Float）"),
    };

    public static int MaxWriteCount(TagDataType type) => Math.Max(1, MaxWriteRegisters / ValueCount(type));

    // ---------------- 解码：数据体字节 → 值 ----------------

    public static object DecodeBits(byte[] data, int bitOffset)
    {
        var index = bitOffset / 8;
        if (index >= data.Length) throw new IOException("线圈数据不足");
        return (data[index] & (1 << (bitOffset % 8))) != 0;
    }

    public static ushort DecodeRegister(byte[] data, int regIndex)
    {
        var i = regIndex * 2;
        if (i + 1 >= data.Length) throw new IOException("寄存器数据不足");
        return (ushort)((data[i] << 8) | data[i + 1]);
    }

    public static object Decode(TagDataType type, byte[] data, int startIndex, bool littleWordOrder) => type switch
    {
        TagDataType.Bool => DecodeBits(data, startIndex),        // 线圈区：位即值
        TagDataType.Int16 => (short)DecodeRegister(data, startIndex),
        TagDataType.UInt16 => DecodeRegister(data, startIndex),
        TagDataType.Int32 => (int)Combine32(data, startIndex, littleWordOrder),
        TagDataType.UInt32 => Combine32(data, startIndex, littleWordOrder),
        TagDataType.Float => BitConverter.Int32BitsToSingle((int)Combine32(data, startIndex, littleWordOrder)),
        TagDataType.Int64 => (long)Combine64(data, startIndex, littleWordOrder),
        TagDataType.UInt64 => Combine64(data, startIndex, littleWordOrder),
        TagDataType.Double => BitConverter.Int64BitsToDouble((long)Combine64(data, startIndex, littleWordOrder)),
        _ => throw new NotSupportedException($"Modbus 不支持数据类型 {type}"),
    };

    private static uint Combine32(byte[] d, int i, bool little)
    {
        var r0 = DecodeRegister(d, i);
        var r1 = DecodeRegister(d, i + 1);
        return little
            ? (uint)((r1 << 16) | r0)
            : (uint)((r0 << 16) | r1);
    }

    private static ulong Combine64(byte[] d, int i, bool little)
    {
        var lo = Combine32(d, i, little);
        var hi = Combine32(d, i + 2, little);
        // 注意先转 ulong：uint << 32 的移位量会按模 32 归零（移了等于没移）
        return little ? ((ulong)hi << 32) | lo : ((ulong)lo << 32) | hi;
    }

    // ---------------- 编码：值 → 寄存器数组 ----------------

    public static ushort[] Encode(object value, bool littleWordOrder)
    {
        var little = littleWordOrder;
        switch (value)
        {
            case bool b: return [(ushort)(b ? 0xFF00 : 0x0000)];
            case short s: return [(ushort)s];
            case ushort u: return [u];
            case int i32:
                {
                    var u = (uint)i32;
                    return little ? [(ushort)u, (ushort)(u >> 16)] : [(ushort)(u >> 16), (ushort)u];
                }
            case uint u32:
                return little ? [(ushort)u32, (ushort)(u32 >> 16)] : [(ushort)(u32 >> 16), (ushort)u32];
            case long l64:
                {
                    var u = (ulong)l64;
                    var pair = Split64(u, little);
                    return pair;
                }
            case ulong u64: return Split64(u64, little);
            case float f:
                {
                    var u = (uint)BitConverter.SingleToInt32Bits(f);
                    return little ? [(ushort)u, (ushort)(u >> 16)] : [(ushort)(u >> 16), (ushort)u];
                }
            case double d:
                {
                    var u = (ulong)BitConverter.DoubleToInt64Bits(d);
                    return Split64(u, little);
                }
            default:
                throw new NotSupportedException($"Modbus 写入不支持值类型 {value?.GetType().Name ?? "null"}");
        }
    }

    private static ushort[] Split64(ulong u, bool little)
    {
        var w0 = (ushort)(u >> 48);
        var w1 = (ushort)(u >> 32);
        var w2 = (ushort)(u >> 16);
        var w3 = (ushort)u;
        return little ? [w3, w2, w1, w0] : [w0, w1, w2, w3];
    }
}
