namespace EvolverCollect.Drivers.S7;

using System.Text.Json;
using EvolverCollect.Core.Abstractions;
using global::S7.Net;
using global::S7.Net.Types;

/// <summary>西门子 S7 驱动（S7netplus）。地址语法：M100 / MW100 / MD100 / DB20.DBX0.0 / DB20.DBW2 / DB20.DBD4。</summary>
public sealed class S7Driver(S7Options options) : IDriver
{
    private Plc? _plc;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private const int MaxItemsPerRead = 19; // PDU 限制，ReadMultipleVars 分批

    public DriverDescriptor Descriptor => S7DriverFactory.DescriptorValue;
    public DriverStats Stats { get; } = new();

    public async Task<ConnectResult> ConnectAsync(CancellationToken ct)
    {
        try
        {
            var cpu = Enum.TryParse<CpuType>(options.CpuType, ignoreCase: true, out var parsedCpu)
                ? parsedCpu : CpuType.S71200;
            var plc = new Plc(cpu, options.Host, (short)options.Rack, (short)options.Slot);
            await plc.OpenAsync(ct);
            if (plc.IsConnected != true)
                return ConnectResult.Fail("S7 连接失败：PLC 无响应（检查 IP/Rack/Slot 与 PUT/GET 权限）");
            _plc = plc;
            return ConnectResult.Ok();
        }
        catch (Exception ex)
        {
            return ConnectResult.Fail($"S7 连接失败: {ex.Message}");
        }
    }

    public Task DisconnectAsync()
    {
        _plc?.Close();
        _plc = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _ioLock.Dispose();
    }

    public ITagBrowser? CreateTagBrowser() => null;

    public async Task<IReadOnlyList<DataPoint>> ReadAsync(DeviceBatch batch, CancellationToken ct)
    {
        var plc = _plc ?? throw new IOException("S7 未连接");
        var ts = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var points = new List<DataPoint>(batch.Tags.Count);

        var items = new List<(TagDefinition Tag, S7Address Addr, DataItem Item)>();
        foreach (var tag in batch.Tags)
        {
            try
            {
                var addr = S7Address.Parse(tag.Address);
                items.Add((tag, addr, new DataItem
                {
                    DataType = addr.DataType,
                    DB = addr.Db,
                    StartByteAdr = addr.StartByte,
                    VarType = MapVarType(tag, addr),
                    BitAdr = addr.Bit,
                    Count = 1,
                }));
            }
            catch (FormatException ex)
            {
                points.Add(DataPoint.BadPoint(batch.ChannelId, batch.DeviceId, tag.Id, batch.DeviceName, tag.Name, ex.Message, ts));
            }
        }

        for (var offset = 0; offset < items.Count; offset += MaxItemsPerRead)
        {
            var chunk = items.Skip(offset).Take(MaxItemsPerRead).ToList();
            try
            {
                await _ioLock.WaitAsync(ct);
                try { await plc.ReadMultipleVarsAsync([.. chunk.Select(i => i.Item)], ct); }
                finally { _ioLock.Release(); }

                for (var i = 0; i < chunk.Count; i++)
                {
                    var (tag, _, item) = chunk[i];
                    var value = item.Value;
                    if (tag.DataType == TagDataType.Bool && value is not bool && value is ushort raw)
                        value = raw; // 寄存器拆位路径：交给点位引擎 BitIndex
                    points.Add(new DataPoint(batch.ChannelId, batch.DeviceId, tag.Id, batch.DeviceName, tag.Name, value, Quality.Good, ts));
                }
                Stats.RecordRead(default, true, chunk.Count);
            }
            catch (Exception ex) // S7netplus 异常统一视为链路级 → 重连
            {
                Stats.RecordRead(default, false, chunk.Count, ex.Message);
                throw new IOException($"S7 读取失败: {ex.Message}", ex);
            }
        }
        return points;
    }

    public async Task<WriteResult> WriteAsync(TagDefinition tag, object value, string deviceName, int? stationNo, CancellationToken ct)
    {
        var plc = _plc ?? throw new IOException("S7 未连接");
        if (!tag.Writable) return WriteResult.Fail("点位未开启可写");
        try
        {
            var addr = S7Address.Parse(tag.Address);
            var converted = tag.DataType switch
            {
                TagDataType.Bool => Convert.ToBoolean(value),
                TagDataType.Int16 => Convert.ToInt16(value),
                TagDataType.UInt16 => Convert.ToUInt16(value),
                TagDataType.Int32 => Convert.ToInt32(value),
                TagDataType.UInt32 => Convert.ToUInt32(value),
                TagDataType.Int64 => Convert.ToInt64(value),
                TagDataType.UInt64 => Convert.ToUInt64(value),
                TagDataType.Float => Convert.ToSingle(value),
                TagDataType.Double => Convert.ToDouble(value),
                _ => value,
            };
            await _ioLock.WaitAsync(ct);
            try { await plc.WriteAsync(addr.Full, converted, ct); }
            finally { _ioLock.Release(); }
            return WriteResult.Ok();
        }
        catch (Exception ex) { return WriteResult.Fail(ex.Message); }
    }

    private static VarType MapVarType(TagDefinition tag, S7Address addr) => tag.DataType switch
    {
        TagDataType.Bool => VarType.Bit,
        TagDataType.Int16 => VarType.Int,
        TagDataType.UInt16 => VarType.Word,
        TagDataType.Int32 => VarType.DInt,
        TagDataType.UInt32 => VarType.DWord,
        TagDataType.Float => VarType.Real,
        TagDataType.Double => VarType.LReal,
        TagDataType.Int64 or TagDataType.UInt64 => throw new NotSupportedException("S7 暂不支持 64 位类型"),
        _ => throw new NotSupportedException($"S7 不支持数据类型 {tag.DataType}（String 请用 S7 专用类型，暂未开放）"),
    };
}

public sealed class S7Options
{
    public string Host { get; set; } = "192.168.0.1";
    public string CpuType { get; set; } = "S71200";
    public int Rack { get; set; } = 0;
    public int Slot { get; set; } = 1;
}

/// <summary>S7 地址解析。</summary>
internal sealed record S7Address(DataType DataType, int Db, int StartByte, byte Bit, string Full)
{
    public static S7Address Parse(string address)
    {
        var s = address?.Trim().ToUpperInvariant() ?? throw new FormatException("地址为空");

        // DB20.DBX100.2 / DB20.DBW100 / DB20.DBD100
        if (s.StartsWith("DB") && s.Contains('.'))
        {
            var first = s.Split('.');
            if (!int.TryParse(first[0][2..], out var db)) throw new FormatException($"DB 号不合法: {first[0]}");
            var part = first[1];
            var areaType = part[..3] switch
            {
                "DBX" => "X",
                "DBW" => "W",
                "DBD" => "D",
                "DBB" => "B",
                _ => throw new FormatException($"DB 内类型必须为 DBX/DBW/DBD/DBB: {part}"),
            };
            var rest = part[3..];
            byte bit = 0;
            if (first.Length > 2)
            {
                if (!byte.TryParse(first[2], out bit)) throw new FormatException($"位编号不合法: {first[2]}");
                areaType = "X";
            }
            if (!int.TryParse(rest, out var start)) throw new FormatException($"起始字节不合法: {rest}");
            var size = areaType == "X" ? 1 : areaType == "B" ? 1 : areaType == "W" ? 2 : 4;
            return new S7Address(DataType.DataBlock, db, start, bit, s) with { };
        }

        // M100 / MB100 / MW100 / MD100 / I0.1 / Q0.1 / IW64
        if (s.Length >= 2 && s[0] is 'M' or 'I' or 'Q')
        {
            var areaChar = s[0];
            var typeChar = s.Length > 1 && (s[1] is 'B' or 'W' or 'D') && !char.IsDigit(s[1]) ? s[1] : 'B';
            var numberPart = typeChar == 'B' && char.IsDigit(s[1]) ? s[1..] : s[2..];
            if (typeChar != 'B' && numberPart.Contains('.'))
            {
                var parts = numberPart.Split('.');
                numberPart = parts[0];
            }
            if (!int.TryParse(numberPart, out var start)) throw new FormatException($"S7 地址不合法: {address}");
            var area = areaChar switch
            {
                'M' => DataType.Memory,
                'I' => DataType.Input,
                _ => DataType.Output,
            };
            return new S7Address(area, 0, start, 0, s);
        }

        throw new FormatException($"无法识别的 S7 地址: {address}（如 M100 / MW100 / DB20.DBD100）");
    }
}

public sealed class S7DriverFactory : IDriverFactory
{
    public const string DriverIdValue = "s7";

    public static DriverDescriptor DescriptorValue { get; } = new(
        DriverIdValue, "西门子 S7", "SIEMENS",
        """
        {
          "type": "object",
          "properties": {
            "host": { "type": "string", "title": "PLC IP", "default": "192.168.0.1" },
            "cpuType": { "type": "string", "title": "CPU 类型",
              "enum": ["S7200Smart", "S7300", "S7400", "S71200", "S71500"], "default": "S71200" },
            "rack": { "type": "integer", "title": "机架号", "default": 0 },
            "slot": { "type": "integer", "title": "槽号", "default": 1,
              "description": "1200/1500 常为 0/1；300 常为 0/2" }
          },
          "required": ["host"]
        }
        """,
        DriverCapabilities.Read | DriverCapabilities.Write,
        "M100/MW100/MD100（M 区字节/字/双字）；DB20.DBX0.0（位）/DB20.DBW2（字）/DB20.DBD4（双字/Float）。1500 需开启 PUT/GET 且 DB 取消优化访问。",
        "protocol-tutorials/s7/01-overview");

    public string DriverId => DriverIdValue;
    public DriverDescriptor Descriptor => DescriptorValue;

    public IDriver Create(string connectionJson)
    {
        var options = JsonSerializer.Deserialize<S7Options>(
            string.IsNullOrWhiteSpace(connectionJson) ? "{}" : connectionJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new S7Options();
        return new S7Driver(options);
    }
}
