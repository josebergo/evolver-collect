namespace EvolverCollect.Drivers.Modbus;

using System.Diagnostics;
using System.Text.Json;
using EvolverCollect.Core.Abstractions;

/// <summary>
/// Modbus 驱动（TCP / RTU 串口 / RTU-over-TCP）。
/// 语义约定：从站明确拒绝（ModbusException）→ 仅相关点位置 Bad 继续；
/// 链路级故障（超时/断开）→ 抛 IOException，由运行时统一断链通告与重连。
/// </summary>
public sealed class ModbusDriver(ModbusOptions options) : IDriver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _ioLock = new(1, 1); // Modbus 严格请求-响应串行
    private IModbusTransport? _transport;
    private ushort _transactionId;

    public DriverDescriptor Descriptor => ModbusDriverFactory.DescriptorValue;
    public DriverStats Stats { get; } = new();

    public async Task<ConnectResult> ConnectAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            IModbusTransport transport = options.Mode switch
            {
                "tcp" => new TcpTransport(options.Host, options.Port),
                "rtu-over-tcp" => new RtuOverTcpTransport(options.Host, options.Port),
                "rtu" => new SerialRtuTransport(options.SerialPort, options.BaudRate,
                    ParseParity(options.Parity), options.DataBits, ParseStopBits(options.StopBits)),
                _ => throw new IOException($"未知的 Modbus 模式: {options.Mode}（支持 tcp / rtu / rtu-over-tcp）"),
            };
            await transport.OpenAsync(ct);
            _transport = transport;

            // 探测：读 HR0 一个寄存器验证从站在线。从站返回异常码也算“链路通”（设备活着只是没这个地址）。
            try
            {
                await _ioLock.WaitAsync(ct);
                try { await TransactReadAsync((byte)options.UnitId, ModbusArea.HoldingRegister, 0, 1, ct); }
                finally { _ioLock.Release(); }
            }
            catch (ModbusException)
            {
                // 设备应答了异常（如 02 非法地址）——连接本身是好的
            }

            return ConnectResult.Ok(sw.Elapsed);
        }
        catch (Exception ex)
        {
            return ConnectResult.Fail($"连接失败: {ex.Message}", sw.Elapsed);
        }
    }

    public Task DisconnectAsync()
    {
        _transport?.Dispose();
        _transport = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _transport?.Dispose();
        _transport = null;
        _ioLock.Dispose();
        await ValueTask.CompletedTask;
    }

    public ITagBrowser? CreateTagBrowser() => null;

    public async Task<IReadOnlyList<DataPoint>> ReadAsync(DeviceBatch batch, CancellationToken ct)
    {
        var unit = (byte)(batch.StationNo ?? options.UnitId);
        var ts = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var points = new List<DataPoint>(batch.Tags.Count);

        // 1. 地址解析：解析失败的点直接 Bad，不影响其他点
        var parsed = new List<(TagDefinition Tag, ModbusAddress Addr)>();
        foreach (var tag in batch.Tags)
        {
            try { parsed.Add((tag, ModbusAddress.Parse(tag.Address))); }
            catch (FormatException ex)
            {
                points.Add(DataPoint.BadPoint(batch.ChannelId, batch.DeviceId, tag.Id,
                    batch.DeviceName, tag.Name, ex.Message, ts));
            }
        }
        if (parsed.Count == 0) return points;

        // 2. 合并读规划 + 逐计划执行
        foreach (var plan in ReadPlanner.Plan(parsed))
        {
            var sw = Stopwatch.StartNew();
            byte[] data;
            try
            {
                data = await ReadPlanAsync(unit, plan, ct);
                sw.Stop();
            }
            catch (ModbusException mex) // 从站拒绝该区间：区间内点位 Bad，继续其他计划
            {
                Stats.RecordRead(sw.Elapsed, false, plan.Items.Count, mex.Message);
                foreach (var (tag, _, _) in plan.Items)
                    points.Add(DataPoint.BadPoint(batch.ChannelId, batch.DeviceId, tag.Id,
                        batch.DeviceName, tag.Name, mex.Message, ts));
                continue;
            }
            // IOException（超时/断链）向上抛 → 运行时断链通告与重连

            foreach (var (tag, addr, _) in plan.Items)
            {
                object value;
                try
                {
                    if (addr.IsRegister)
                    {
                        var regIndex = addr.Offset - plan.Start;
                        // 寄存器区的 Bool = 原始寄存器值，由点位引擎按 BitIndex 拆位
                        value = tag.DataType == TagDataType.Bool
                            ? ModbusValues.DecodeRegister(data, regIndex)
                            : ModbusValues.Decode(tag.DataType, data, regIndex, options.LittleWordOrder);
                    }
                    else
                    {
                        if (tag.DataType != TagDataType.Bool)
                            throw new NotSupportedException("线圈/离散输入区只支持 Bool 类型");
                        value = ModbusValues.DecodeBits(data, addr.Offset - plan.Start);
                    }
                }
                catch (Exception ex)
                {
                    points.Add(DataPoint.BadPoint(batch.ChannelId, batch.DeviceId, tag.Id,
                        batch.DeviceName, tag.Name, ex.Message, ts));
                    continue;
                }
                points.Add(new DataPoint(batch.ChannelId, batch.DeviceId, tag.Id,
                    batch.DeviceName, tag.Name, value, Quality.Good, ts));
            }
            Stats.RecordRead(sw.Elapsed, true, plan.Items.Count);
        }
        return points;
    }

    public async Task<WriteResult> WriteAsync(TagDefinition tag, object value, string deviceName, int? stationNo, CancellationToken ct)
    {
        ModbusAddress addr;
        try { addr = ModbusAddress.Parse(tag.Address); }
        catch (FormatException ex) { return WriteResult.Fail(ex.Message); }

        if (addr.Area is ModbusArea.DiscreteInput or ModbusArea.InputRegister)
            return WriteResult.Fail("输入区只读，不能写入");

        if (!tag.Writable) return WriteResult.Fail("点位未开启可写（Writable）");

        var unit = (byte)(stationNo ?? options.UnitId);
        var little = options.LittleWordOrder;
        try
        {
            await _ioLock.WaitAsync(ct);
            try
            {
                if (addr.Area == ModbusArea.Coil)
                {
                    var b = Convert.ToBoolean(value);
                    await WriteCoilAsync(unit, addr.Offset, b, ct);
                }
                else
                {
                    var regs = ModbusValues.Encode(value, little);
                    if (regs.Length == 1)
                        await WriteSingleRegisterAsync(unit, addr.Offset, regs[0], ct);
                    else
                        await WriteMultipleRegistersAsync(unit, addr.Offset, regs, ct);
                }
                return WriteResult.Ok();
            }
            finally { _ioLock.Release(); }
        }
        catch (ModbusException mex) { return WriteResult.Fail(mex.Message); }
        catch (Exception ex) { return WriteResult.Fail(ex.Message); }
    }

    // ---------- 传输原语 ----------

    private async Task<byte[]> ReadPlanAsync(byte unit, ReadPlan plan, CancellationToken ct)
    {
        await _ioLock.WaitAsync(ct);
        try
        {
            var (data, _) = await TransactReadAsync(unit, plan.Area, plan.Start, plan.Count, ct);
            return data;
        }
        finally { _ioLock.Release(); }
    }

    private async Task<(byte[] Data, ushort TransactionId)> TransactReadAsync(
        byte unit, ModbusArea area, ushort start, ushort count, CancellationToken ct, ushort? forcedTid = null)
    {
        var transport = _transport ?? throw new IOException("Modbus 未连接");
        var isTcp = options.Mode is "tcp";
        var tid = forcedTid ?? NextTransactionId();

        var request = isTcp
            ? ModbusCodec.BuildTcpRead(tid, unit, area, start, count)
            : ModbusCodec.BuildRtuRead(unit, area, start, count);

        var frame = await transport.TransactAsync(request, options.TimeoutMs, ct);

        var data = isTcp
            ? ModbusCodec.ParseTcpReadResponse(frame, tid, unit).ToArray()
            : ModbusCodec.ParseRtuReadResponse(frame, unit);
        return (data, tid);
    }

    private async Task WriteCoilAsync(byte unit, ushort address, bool value, CancellationToken ct)
    {
        var transport = _transport ?? throw new IOException("Modbus 未连接");
        var isTcp = options.Mode is "tcp";
        var tid = NextTransactionId();
        var request = isTcp
            ? ModbusCodec.BuildTcpWriteSingleCoil(tid, unit, address, value)
            : ModbusCodec.BuildRtuWriteSingleCoil(unit, address, value);
        var frame = await transport.TransactAsync(request, options.TimeoutMs, ct);
        if (isTcp) ModbusCodec.ParseTcpWriteResponse(frame, tid, unit, ModbusCodec.WriteSingleCoil, address);
        else ModbusCodec.ParseRtuWriteResponse(frame, unit, ModbusCodec.WriteSingleCoil, address);
    }

    private async Task WriteSingleRegisterAsync(byte unit, ushort address, ushort value, CancellationToken ct)
    {
        var transport = _transport ?? throw new IOException("Modbus 未连接");
        var isTcp = options.Mode is "tcp";
        var tid = NextTransactionId();
        var request = isTcp
            ? ModbusCodec.BuildTcpWriteSingleRegister(tid, unit, address, value)
            : ModbusCodec.BuildRtuWriteSingleRegister(unit, address, value);
        var frame = await transport.TransactAsync(request, options.TimeoutMs, ct);
        if (isTcp) ModbusCodec.ParseTcpWriteResponse(frame, tid, unit, ModbusCodec.WriteSingleRegister, address);
        else ModbusCodec.ParseRtuWriteResponse(frame, unit, ModbusCodec.WriteSingleRegister, address);
    }

    private async Task WriteMultipleRegistersAsync(byte unit, ushort address, ushort[] values, CancellationToken ct)
    {
        var transport = _transport ?? throw new IOException("Modbus 未连接");
        var isTcp = options.Mode is "tcp";
        var tid = NextTransactionId();
        var request = isTcp
            ? ModbusCodec.BuildTcpWriteMultipleRegisters(tid, unit, address, values)
            : ModbusCodec.BuildRtuWriteMultipleRegisters(unit, address, values);
        var frame = await transport.TransactAsync(request, options.TimeoutMs, ct);
        if (isTcp) ModbusCodec.ParseTcpWriteResponse(frame, tid, unit, ModbusCodec.WriteMultipleRegisters, address);
        else ModbusCodec.ParseRtuWriteResponse(frame, unit, ModbusCodec.WriteMultipleRegisters, address);
    }

    private ushort NextTransactionId() => ++_transactionId == 0 ? ++_transactionId : _transactionId;

    internal static System.IO.Ports.Parity ParseParity(string s) => s.ToLowerInvariant() switch
    {
        "none" => System.IO.Ports.Parity.None,
        "odd" => System.IO.Ports.Parity.Odd,
        _ => System.IO.Ports.Parity.Even,
    };

    internal static System.IO.Ports.StopBits ParseStopBits(double s) => s switch
    {
        1.5 => System.IO.Ports.StopBits.OnePointFive,
        2 => System.IO.Ports.StopBits.Two,
        _ => System.IO.Ports.StopBits.One,
    };
}

/// <summary>Modbus 连接参数（通道 ConnectionJson 的反序列化目标）。</summary>
public sealed class ModbusOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 502;
    /// <summary>默认从站号（设备上未配置 StationNo 时使用）。</summary>
    public int UnitId { get; set; } = 1;
    /// <summary>tcp | rtu | rtu-over-tcp。</summary>
    public string Mode { get; set; } = "tcp";
    public int TimeoutMs { get; set; } = 3000;
    /// <summary>32 位以上值的字序：false=ABCD（标准大端）、true=CDAB（字交换，部分国产设备）。</summary>
    public bool LittleWordOrder { get; set; }
    // ---- 仅 rtu 模式 ----
    public string SerialPort { get; set; } = "COM1";
    public int BaudRate { get; set; } = 9600;
    public string Parity { get; set; } = "even";
    public int DataBits { get; set; } = 8;
    public double StopBits { get; set; } = 1;
}
