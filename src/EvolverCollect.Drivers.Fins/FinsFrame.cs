namespace EvolverCollect.Drivers.Fins;

using System.Buffers.Binary;
using System.Text.Json;
using EvolverCollect.Core.Abstractions;

/// <summary>FINS 帧构造与解析：帧头 10 字节（ICF RSV GCT DNA DA1 DA2 SNA SA1 SA2 SID）+ 命令。</summary>
public static class FinsFrame
{
    public static byte[] BuildMemoryRead(FinsArea area, int start, ushort count, byte serverNode, byte clientNode, byte sid)
    {
        var frame = new byte[10 + 2 + 5];
        WriteHeader(frame, serverNode, clientNode, sid);
        frame[10] = 0x01; frame[11] = 0x01; // 存储区读
        frame[12] = (byte)area;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(13), (ushort)start);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(15), count);
        return frame;
    }

    public static byte[] BuildMemoryWrite(FinsArea area, int start, ushort[] words, byte serverNode, byte clientNode, byte sid)
    {
        var frame = new byte[10 + 2 + 5 + words.Length * 2];
        WriteHeader(frame, serverNode, clientNode, sid);
        frame[10] = 0x01; frame[11] = 0x02; // 存储区写
        frame[12] = (byte)area;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(13), (ushort)start);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(15), (ushort)words.Length);
        for (var i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(17 + i * 2), words[i]);
        return frame;
    }

    private static void WriteHeader(byte[] frame, byte serverNode, byte clientNode, byte sid)
    {
        frame[0] = 0x80;  // ICF：需要响应
        frame[1] = 0x00;  // RSV
        frame[2] = 0x02;  // GCT
        frame[3] = 0x00;  // DNA：本地网络
        frame[4] = serverNode;
        frame[5] = 0x00;  // DA2：CPU 单元
        frame[6] = 0x00;  // SNA
        frame[7] = clientNode;
        frame[8] = 0x00;  // SA2
        frame[9] = sid;   // SID
    }

    public static byte[] ParseMemoryReadResponse(byte[] body, int expectedLength)
    {
        if (body.Length < 12) throw new IOException($"FINS 响应帧不完整（{body.Length}）");
        var endCode = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(10));
        if (endCode != 0) throw new FinsException(DescribeEndCode(endCode), endCode);
        if (body.Length < 12 + expectedLength) throw new IOException("FINS 响应数据长度不符");
        return body[12..(12 + expectedLength)];
    }

    public static void ParseWriteResponse(byte[] body)
    {
        if (body.Length < 12) throw new IOException("FINS 写响应帧不完整");
        var endCode = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(10));
        if (endCode != 0) throw new FinsException(DescribeEndCode(endCode), endCode);
    }

    private static string DescribeEndCode(ushort code) => code switch
    {
        0x0101 => "FINS 结束码 0101：服务未定义（命令错）",
        0x0104 => "FINS 结束码 0104：地址范围越界（检查区代码与偏移）",
        0x0106 => "FINS 结束码 0106：无响应超时（PLC 未开放访问）",
        0x1100 => "FINS 结束码 1100：只读区禁止写入",
        0x1101 => "FINS 结束码 1101：写区只读/区代码错",
        0x1103 => "FINS 结束码 1103：数据长度错",
        _ => $"FINS 结束码异常: {code:X4}",
    };
}

/// <summary>FINS 存储区代码（字访问/位访问成对出现）。</summary>
public enum FinsArea : byte
{
    CioWord = 0xB0, CioBit = 0x30,
    WorkWord = 0xB1, WorkBit = 0x31,
    HoldingWord = 0xB2, HoldingBit = 0x32,
    DmWord = 0x82, DmBit = 0x02,

    // 扩展方法风格成员无法放 enum，见 FinsAreaExtensions
}

public static class FinsAreaExtensions
{
    public static bool IsBitArea(this FinsArea area) =>
        area is FinsArea.CioBit or FinsArea.WorkBit or FinsArea.HoldingBit or FinsArea.DmBit;

    public static FinsArea WordCounterpart(this FinsArea area) => area switch
    {
        FinsArea.CioBit => FinsArea.CioWord,
        FinsArea.WorkBit => FinsArea.WorkWord,
        FinsArea.HoldingBit => FinsArea.HoldingWord,
        FinsArea.DmBit => FinsArea.DmWord,
        _ => area,
    };
}

public sealed record FinsAddress(FinsArea Area, int Offset, int BitAddress)
{
    public static FinsAddress Parse(string address)
    {
        var s = address?.Trim().ToUpperInvariant() ?? throw new FormatException("地址为空");
        var (prefix, area) = s switch
        {
        ['C', 'I', 'O', ..] => ("CIO", FinsArea.CioWord),
            ['W', ..] => ("W", FinsArea.WorkWord),
            ['H', ..] => ("H", FinsArea.HoldingWord),
            ['D', ..] => ("D", FinsArea.DmWord),
            _ => throw new FormatException($"无法识别的 FINS 地址: {address}（支持 CIO/W/H/D）"),
        };
        var rest = s[prefix.Length..];
        int bit = 0;
        var wordPart = rest;
        if (rest.Contains('.'))
        {
            var parts = rest.Split('.');
            wordPart = parts[0];
            bit = int.TryParse(parts[1], out var b) ? b : throw new FormatException($"位编号不合法: {parts[1]}");
        }
        if (!int.TryParse(wordPart, out var word)) throw new FormatException($"软元件编号不合法: {wordPart}");
        var isBit = rest.Contains('.');
        var resolved = isBit ? area switch
        {
            FinsArea.CioWord => FinsArea.CioBit,
            FinsArea.WorkWord => FinsArea.WorkBit,
            FinsArea.HoldingWord => FinsArea.HoldingBit,
            _ => FinsArea.DmBit,
        } : area;
        return new FinsAddress(resolved, word, word * 16 + bit);
    }
}

public static class FinsValues
{
    public static int WordCount(TagDataType type) => type switch
    {
        TagDataType.Int16 or TagDataType.UInt16 => 1,
        TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float => 2,
        TagDataType.Int64 or TagDataType.UInt64 or TagDataType.Double => 4,
        _ => throw new NotSupportedException($"FINS 不支持数据类型 {type}"),
    };

    public static object Decode(TagDataType type, byte[] data, int byteIndex)
    {
        ushort Word(int i) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(byteIndex + i * 2));
        return type switch
        {
            TagDataType.Int16 => (object)(short)Word(0),
            TagDataType.UInt16 => Word(0),
            TagDataType.Int32 => (short)Word(0) << 16 | Word(1),
            TagDataType.UInt32 => (uint)(Word(0) << 16 | Word(1)),
            TagDataType.Float => BitConverter.Int32BitsToSingle((int)((Word(0) << 16) | Word(1))),
            TagDataType.Int64 => (long)Combine64(),
            TagDataType.UInt64 => Combine64(),
            TagDataType.Double => BitConverter.Int64BitsToDouble((long)Combine64()),
            _ => throw new NotSupportedException($"FINS 不支持数据类型 {type}"),
        };

        ulong Combine64() =>
            (ulong)Word(0) << 48 | (ulong)Word(1) << 32 | (ulong)Word(2) << 16 | Word(3);
    }

    public static ushort[] Encode(TagDataType type, object value)
    {
        switch (value)
        {
            case bool b: return [(ushort)(b ? 1 : 0)];
            case short s: return [(ushort)s];
            case ushort u: return [u];
            case int i32:
                {
                    var u = (uint)i32;
                    return [(ushort)(u >> 16), (ushort)u];
                }
            case uint ui: return [(ushort)(ui >> 16), (ushort)ui];
            case long l: return Split64((ulong)l);
            case ulong ul: return Split64(ul);
            case float f:
                {
                    var u = (uint)BitConverter.SingleToInt32Bits(f);
                    return [(ushort)(u >> 16), (ushort)u];
                }
            case double d: return Split64((ulong)BitConverter.DoubleToInt64Bits(d));
            default: throw new NotSupportedException($"FINS 写入不支持类型 {value?.GetType().Name}");
        }
    }

    private static ushort[] Split64(ulong u) =>
        [(ushort)(u >> 48), (ushort)(u >> 32), (ushort)(u >> 16), (ushort)u];
}

public sealed class FinsDriverFactory : IDriverFactory
{
    public const string DriverIdValue = "fins";

    public static DriverDescriptor DescriptorValue { get; } = new(
        DriverIdValue, "欧姆龙 FINS/TCP", "OMRON",
        """
        {
          "type": "object",
          "properties": {
            "host": { "type": "string", "title": "PLC IP", "default": "192.168.1.5" },
            "port": { "type": "integer", "title": "端口", "default": 9600 },
            "clientNode": { "type": "integer", "title": "本机节点号", "description": "留空自动" },
            "serverNode": { "type": "integer", "title": "PLC 节点号", "description": "留空取握手应答" },
            "timeoutMs": { "type": "integer", "title": "超时(ms)", "default": 3000 }
          },
          "required": ["host"]
        }
        """,
        DriverCapabilities.Read | DriverCapabilities.Write,
        "地址：D100（DM 字）/ CIO50 / W10 / H5；位访问写 D100.2（字×16+位）。",
        "protocol-tutorials/fins/01-overview");

    public string DriverId => DriverIdValue;
    public DriverDescriptor Descriptor => DescriptorValue;

    public IDriver Create(string connectionJson)
    {
        var options = JsonSerializer.Deserialize<FinsOptions>(
            string.IsNullOrWhiteSpace(connectionJson) ? "{}" : connectionJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new FinsOptions();
        return new FinsDriver(options);
    }
}
