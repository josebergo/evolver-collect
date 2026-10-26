namespace EvolverCollect.Drivers.Mc;

using System.Buffers.Binary;
using System.Text.Json;
using EvolverCollect.Core.Abstractions;

/// <summary>MC 3E 帧构造与解析（Q/L/R 系列 Binary 格式）。帧头：50 00 00 FF FF 03 00 + 数据长(2B LE) + 监视定时器(2B LE)。</summary>
public static class McFrame
{
    public const int MaxWordsPerRead = 960; // Q 系列 Words/Block 上限

    private static readonly byte[] Head = [0x50, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00];
    private const ushort Watchdog = 0x0010; // 16×250ms

    // ---------------- 读 ----------------

    public static byte[] BuildWordRead(byte device, int offset, ushort count, byte sid)
    {
        // cmd 0104 sub 0001 + 软元件编号(3B LE) + 代码(1B) + 点数(2B LE)
        var body = new byte[2 + 2 + 3 + 1 + 2];
        body[0] = 0x01; body[1] = 0x04; body[2] = 0x00; body[3] = 0x01;
        WriteDevice(body, 4, device, offset);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(8), count);
        return Wrap(body);
    }

    public static byte[] BuildBitRead(byte device, int offset, ushort count, byte sid) =>
        // 位读与字读同命令（0104/0001），由软元件代码区分
        BuildWordRead(device, offset, count, sid);

    public static ushort[] ParseWordReadResponse(byte[] frame, ushort wordCount)
    {
        var body = ParseResponse(frame, expectedDataLength: wordCount * 2);
        var words = new ushort[wordCount];
        for (var i = 0; i < wordCount; i++)
            words[i] = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(i * 2));
        return words;
    }

    public static bool[] ParseBitReadResponse(byte[] frame, ushort bitCount)
    {
        var body = ParseResponse(frame, bitCount); // MC 位读：每点 1 字节
        return [.. body.Select(b => (b & 0x10) != 0 || b == 0x01)];
    }

    // ---------------- 写 ----------------

    public static byte[] BuildWordWrite(byte device, int offset, ushort[] words, byte sid)
    {
        var body = new byte[2 + 2 + 3 + 1 + 2 + words.Length * 2];
        body[0] = 0x14; body[1] = 0x01; body[2] = 0x00; body[3] = 0x01;
        WriteDevice(body, 4, device, offset);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(8), (ushort)words.Length);
        for (var i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(10 + i * 2), words[i]);
        return Wrap(body);
    }

    public static byte[] BuildBitWrite(byte device, int offset, bool[] bits, byte sid)
    {
        var body = new byte[2 + 2 + 3 + 1 + 2 + bits.Length];
        body[0] = 0x14; body[1] = 0x01; body[2] = 0x00; body[3] = 0x02; // sub 0002 = 位写
        WriteDevice(body, 4, device, offset);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(8), (ushort)bits.Length);
        for (var i = 0; i < bits.Length; i++)
            body[10 + i] = (byte)(bits[i] ? 0x10 : 0x00);
        return Wrap(body);
    }

    public static void ParseWriteResponse(byte[] frame) => ParseResponse(frame, 0);

    // ---------------- 公共 ----------------

    private static byte[] Wrap(byte[] body)
    {
        var frame = new byte[Head.Length + 2 + 2 + body.Length];
        Head.CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(7), (ushort)(2 + body.Length)); // 监视定时器+本体
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(9), Watchdog);
        body.CopyTo(frame, 11);
        return frame;
    }

    private static byte[] ParseResponse(byte[] frame, int expectedDataLength)
    {
        // 响应头：D0 00 00 FF FF 03 00 + 长度(2B LE) + 结束码(2B LE) [+ 数据]
        if (frame.Length < 11) throw new IOException($"MC 响应帧不完整（{frame.Length}）");
        if (frame[0] != 0xD0) throw new IOException($"MC 响应副头部异常: {frame[0]:X2}");
        var len = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(7));
        var endCode = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(9)); // 结束码为大端
        if (endCode != 0)
            throw new McException(DescribeEndCode(endCode), endCode);
        if (frame.Length < 11 + expectedDataLength || len < 2 + expectedDataLength)
            throw new IOException("MC 响应数据长度不符");
        return frame[11..(11 + expectedDataLength)];
    }

    private static void WriteDevice(byte[] body, int at, byte device, int offset)
    {
        body[at + 3] = device;
        body[at] = (byte)(offset & 0xFF);
        body[at + 1] = (byte)((offset >> 8) & 0xFF);
        body[at + 2] = (byte)((offset >> 16) & 0xFF);
    }

    private static string DescribeEndCode(ushort code) => code switch
    {
        0xC001 => "MC 结束码 C001：软元件编号非法",
        0xC002 => "MC 结束码 C002：数据长度非法",
        0xC003 => "MC 结束码 C003：命令非法（检查以太网模块协议设置）",
        0xC004 => "MC 结束码 C004：软元件代码非法",
        0xC006 => "MC 结束码 C006：点数超限（单次最多 960 字）",
        0xC00B => "MC 结束码 C00B：命令子编号非法",
        0xC01B => "MC 结束码 C01B：监视定时器超时（PLC 忙）",
        0xC020 => "MC 结束码 C020：互锁中，请稍后重试",
        _ => $"MC 结束码异常: {code:X4}",
    };
}

/// <summary>MC 软元件代码表 + 地址解析（D/W 字软元件，M/L/B/X/Y 位软元件）。</summary>
public enum McDevice : byte
{
    D = 0xA8,   // 数据寄存器（字）
    W = 0xB4,   // 链接寄存器（字）
    M = 0x90,   // 内部继电器（位）
    L = 0x92,   // 链接继电器（位）
    B = 0x0A,   // 链接特殊继电器（位）
    X = 0x9C,   // 输入（位，十六进制编号）
    Y = 0x9D,   // 输出（位，十六进制编号）
}

public sealed record McAddress(McDevice Device, int Offset, bool IsBit)
{
    private static readonly (string Prefix, McDevice Device, bool Bit)[] Map =
    [
        ("D", McDevice.D, false), ("W", McDevice.W, false),
        ("M", McDevice.M, true), ("L", McDevice.L, true), ("B", McDevice.B, true),
    ];

    public static McAddress Parse(string address)
    {
        var s = address?.Trim().ToUpperInvariant() ?? throw new FormatException("地址为空");
        foreach (var (prefix, device, bit) in Map)
        {
            if (s.StartsWith(prefix) && int.TryParse(s[prefix.Length..], out var number))
                return new McAddress(device, number, bit);
        }
        // X/Y 用十六进制编号
        if (s.StartsWith('X') && int.TryParse(s[1..], System.Globalization.NumberStyles.HexNumber, null, out var x))
            return new McAddress(McDevice.X, x, true);
        if (s.StartsWith('Y') && int.TryParse(s[1..], System.Globalization.NumberStyles.HexNumber, null, out var y))
            return new McAddress(McDevice.Y, y, true);
        throw new FormatException($"无法识别的 MC 地址: {address}（支持 D/W/M/L/B（十进制）与 X/Y（十六进制））");
    }

    /// <summary>该点位占用的字数（位软元件走位读路径，不占字）。</summary>
    public int WordCount() => IsBit ? 0 : 1;
}

/// <summary>MC 数值转换：数据为大端字序列，32/64 位值为大端字串。</summary>
public static class McValues
{
    public static int WordCount(TagDataType type) => type switch
    {
        TagDataType.Int16 or TagDataType.UInt16 => 1,
        TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float => 2,
        TagDataType.Int64 or TagDataType.UInt64 or TagDataType.Double => 4,
        _ => throw new NotSupportedException($"MC 不支持数据类型 {type}（String 暂不支持）"),
    };

    public static object DecodeWords(TagDataType type, ushort[] words, int index)
    {
        Span<ushort> span = words;
        var slice = span[index..];
        return type switch
        {
            TagDataType.Int16 => (object)(short)slice[0],
            TagDataType.UInt16 => slice[0],
            TagDataType.Int32 => (short)slice[0] << 16 | slice[1],
            TagDataType.UInt32 => (uint)(slice[0] << 16 | slice[1]),
            TagDataType.Float => BitConverter.Int32BitsToSingle((int)((slice[0] << 16) | slice[1])),
            TagDataType.Int64 => (long)Combine64(slice),
            TagDataType.UInt64 => Combine64(slice),
            TagDataType.Double => BitConverter.Int64BitsToDouble((long)Combine64(slice)),
            TagDataType.Bool => slice[0],
            _ => throw new NotSupportedException($"MC 不支持数据类型 {type}"),
        };
    }

    public static ushort[] EncodeWords(TagDataType type, object value)
    {
        switch (value)
        {
            case bool b: return [(ushort)(b ? 1 : 0)];
            case short s: return [(ushort)s];
            case ushort u: return [u];
            case int i:
                {
                    var u = (uint)i;
                    return [(ushort)(u >> 16), (ushort)u];
                }
            case uint ui:
                return [(ushort)(ui >> 16), (ushort)ui];
            case long l: return Split64((ulong)l);
            case ulong ul: return Split64(ul);
            case float f:
                {
                    var u = (uint)BitConverter.SingleToInt32Bits(f);
                    return [(ushort)(u >> 16), (ushort)u];
                }
            case double d: return Split64((ulong)BitConverter.DoubleToInt64Bits(d));
            default: throw new NotSupportedException($"MC 写入不支持类型 {value?.GetType().Name}");
        }
    }

    private static ulong Combine64(ReadOnlySpan<ushort> w) =>
        (ulong)w[0] << 48 | (ulong)w[1] << 32 | (ulong)w[2] << 16 | w[3];

    private static ushort[] Split64(ulong u) =>
        [(ushort)(u >> 48), (ushort)(u >> 32), (ushort)(u >> 16), (ushort)u];
}

public sealed class McDriverFactory : IDriverFactory
{
    public const string DriverIdValue = "mc";

    public static DriverDescriptor DescriptorValue { get; } = new(
        DriverIdValue, "三菱 MC（Q/L/R）", "MITSUBISHI",
        """
        {
          "type": "object",
          "properties": {
            "host": { "type": "string", "title": "PLC IP", "default": "192.168.1.10" },
            "port": { "type": "integer", "title": "端口", "default": 5000 },
            "timeoutMs": { "type": "integer", "title": "超时(ms)", "default": 3000 }
          },
          "required": ["host"]
        }
        """,
        DriverCapabilities.Read | DriverCapabilities.Write,
        "软元件：D100/W100（字）M100/L100（位）X0/Y10（位，十六进制编号）。32 位占 2 个字。",
        "protocol-tutorials/mc/01-overview");

    public string DriverId => DriverIdValue;
    public DriverDescriptor Descriptor => DescriptorValue;

    public IDriver Create(string connectionJson)
    {
        var options = JsonSerializer.Deserialize<McOptions>(
            string.IsNullOrWhiteSpace(connectionJson) ? "{}" : connectionJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new McOptions();
        return new McDriver(options);
    }
}
