namespace EvolverCollect.Simulator;

using System.Net.Sockets;
using System.Net;
using EvolverCollect.Drivers.Modbus;

public enum SimFraming
{
    /// <summary>标准 Modbus TCP（MBAP 头）。</summary>
    ModbusTcp,
    /// <summary>RTU 帧走 TCP（模拟串口服务器透传）。</summary>
    RtuOverTcp,
}

/// <summary>
/// 模拟 Modbus 从站（TCP）：实现 FC01/02/03/04/05/06/16，四种数据区各 10000 个。
/// 既是无设备用户的演示环境，也是驱动集成测试的对端。
/// 处理流程：剥封装（MBAP/RTU）→ PDU 处理 → 重新封装。
/// </summary>
public sealed class ModbusSimulatorServer : IAsyncDisposable
{
    private readonly ushort[] _holding = new ushort[10000];
    private readonly ushort[] _input = new ushort[10000];
    private readonly bool[] _coils = new bool[10000];
    private readonly bool[] _discrete = new bool[10000];
    private readonly object _bank = new();
    private readonly List<TcpClient> _clients = [];
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public int Port { get; }
    public SimFraming Framing { get; }
    public bool VerboseLog { get; set; }

    /// <summary>实际监听端口（构造传 0 时为系统分配的临时端口）。</summary>
    public int EffectivePort => (IPEndPoint?)_listener?.LocalEndpoint is { } ep ? ep.Port : Port;

    public ModbusSimulatorServer(int port = 1502, SimFraming framing = SimFraming.ModbusTcp)
    {
        Port = port;
        Framing = framing;
    }

    // ---------------- 数据区读写（发生器与测试用） ----------------

    public void SetHolding(int address, ushort value) { lock (_bank) _holding[address] = value; }
    public void SetInput(int address, ushort value) { lock (_bank) _input[address] = value; }
    public void SetCoil(int address, bool value) { lock (_bank) _coils[address] = value; }
    public void SetDiscrete(int address, bool value) { lock (_bank) _discrete[address] = value; }

    public ushort GetHolding(int address) { lock (_bank) return _holding[address]; }
    public bool GetCoil(int address) { lock (_bank) return _coils[address]; }

    public void SetHoldingFloat(int address, float value)
    {
        var bits = (uint)BitConverter.SingleToInt32Bits(value);
        lock (_bank)
        {
            _holding[address] = (ushort)(bits >> 16);
            _holding[address + 1] = (ushort)bits;
        }
    }

    // ---------------- 服务 ----------------

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch { break; }
            lock (_clients) _clients.Add(client);
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            var stream = client.GetStream();
            var buffer = new byte[300];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    byte[] request;
                    if (Framing == SimFraming.ModbusTcp)
                    {
                        // MBAP：7 字节头 + 按 len 字段读余量
                        var header = new byte[7];
                        await ReadExactlyAsync(stream, header, ct);
                        var len = (header[4] << 8) | header[5];
                        if (len is < 2 or > 260) continue;
                        var rest = new byte[len - 1];
                        await ReadExactlyAsync(stream, rest, ct);
                        request = [.. header, .. rest];
                    }
                    else
                    {
                        // RTU 帧走 TCP：按功能码推算完整帧长（与真实串口透传场景一致）
                        var read = 0;
                        while (true)
                        {
                            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
                            if (n == 0) return;
                            read += n;
                            if (TryRtuFrameLength(buffer, read, out var len) && read >= len)
                            {
                                request = buffer[..len];
                                break;
                            }
                            if (read > 260) return;
                        }
                    }

                    var response = HandleFrame(request);
                    if (response.Length > 0)
                    {
                        if (VerboseLog)
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] RX {Convert.ToHexString(request)}  TX {Convert.ToHexString(response)}");
                        await stream.WriteAsync(response, ct);
                    }
                }
            }
            catch (IOException) { /* 客户端断开 */ }
            catch (OperationCanceledException) { }
            lock (_clients) _clients.Remove(client);
        }
    }

    private static bool TryRtuFrameLength(byte[] b, int read, out int len)
    {
        len = -1;
        if (read < 2) return false;
        var known = b[1] switch
        {
            0x01 or 0x02 or 0x03 or 0x04 or 0x05 or 0x06 => (int?)8,
            0x10 => read >= 7 ? 9 + b[6] : null, // 站+fc+addr2+qty2+count+data+crc2
            _ => null,
        };
        if (known is null) return false;
        len = known.Value;
        return true;
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) throw new IOException("客户端断开");
            read += n;
        }
    }

    private byte[] HandleFrame(byte[] request)
    {
        if (Framing == SimFraming.ModbusTcp)
        {
            var tid = (ushort)((request[0] << 8) | request[1]);
            var unit = request[6];
            var pdu = HandlePdu(request[7..], unit);
            var frame = new byte[7 + pdu.Length];
            frame[0] = (byte)(tid >> 8); frame[1] = (byte)tid;
            frame[4] = (byte)((pdu.Length + 1) >> 8); frame[5] = (byte)(pdu.Length + 1);
            frame[6] = unit;
            pdu.CopyTo(frame, 7);
            return frame;
        }
        else
        {
            if (request.Length < 4 || !ModbusCrc16.Validate(request)) return []; // CRC 错：静默丢弃（真实从站行为）
            var unit = request[0];
            var responsePdu = HandlePdu(request[1..^2], unit);
            var frame = new byte[1 + responsePdu.Length + 2];
            frame[0] = unit;
            responsePdu.CopyTo(frame, 1);
            var crc = ModbusCrc16.Compute(frame[..^2]);
            frame[^2] = (byte)(crc & 0xFF);
            frame[^1] = (byte)(crc >> 8);
            return frame;
        }
    }

    /// <summary>PDU 处理（与封装无关，纯功能码逻辑）。</summary>
    private byte[] HandlePdu(byte[] p, byte unit)
    {
        var fc = p[0];
        return fc switch
        {
            0x01 => ReadBits(_coils, p, fc),
            0x02 => ReadBits(_discrete, p, fc),
            0x03 => ReadRegisters(_holding, p, fc),
            0x04 => ReadRegisters(_input, p, fc),
            0x05 => WriteSingleCoil(p, fc),
            0x06 => WriteSingleRegister(p, fc),
            0x10 => WriteMultipleRegisters(p, fc),
            _ => Except(fc, 1),
        };
    }

    private byte[] ReadBits(bool[] bank, byte[] p, byte fc)
    {
        var address = (ushort)((p[1] << 8) | p[2]);
        var count = (ushort)((p[3] << 8) | p[4]);
        if (count == 0 || count > 2000) return Except(fc, 3);
        if (address + count > bank.Length) return Except(fc, 2);

        var byteCount = (count + 7) / 8;
        var data = new byte[byteCount];
        lock (_bank)
        {
            for (var i = 0; i < count; i++)
                if (bank[address + i])
                    data[i / 8] |= (byte)(1 << (i % 8));
        }
        return [fc, (byte)byteCount, .. data];
    }

    private byte[] ReadRegisters(ushort[] bank, byte[] p, byte fc)
    {
        var address = (ushort)((p[1] << 8) | p[2]);
        var count = (ushort)((p[3] << 8) | p[4]);
        if (count == 0 || count > 125) return Except(fc, 3);
        if (address + count > bank.Length) return Except(fc, 2);

        var data = new byte[count * 2];
        lock (_bank)
        {
            for (var i = 0; i < count; i++)
            {
                data[i * 2] = (byte)(bank[address + i] >> 8);
                data[i * 2 + 1] = (byte)(bank[address + i] & 0xFF);
            }
        }
        return [fc, (byte)(count * 2), .. data];
    }

    private byte[] WriteSingleCoil(byte[] p, byte fc)
    {
        var address = (ushort)((p[1] << 8) | p[2]);
        var value = p[3] == 0xFF;
        if (address >= _coils.Length) return Except(fc, 2);
        lock (_bank) _coils[address] = value;
        return [.. p[..5]];
    }

    private byte[] WriteSingleRegister(byte[] p, byte fc)
    {
        var address = (ushort)((p[1] << 8) | p[2]);
        var value = (ushort)((p[3] << 8) | p[4]);
        if (address >= _holding.Length) return Except(fc, 2);
        lock (_bank) _holding[address] = value;
        return [.. p[..5]];
    }

    private byte[] WriteMultipleRegisters(byte[] p, byte fc)
    {
        var address = (ushort)((p[1] << 8) | p[2]);
        var count = (ushort)((p[3] << 8) | p[4]);
        var byteCount = p[5];
        if (byteCount != count * 2) return Except(fc, 3);
        if (address + count > _holding.Length) return Except(fc, 2);
        lock (_bank)
        {
            for (var i = 0; i < count; i++)
                _holding[address + i] = (ushort)((p[6 + i * 2] << 8) | p[7 + i * 2]);
        }
        return [fc, p[1], p[2], p[3], p[4]];
    }

    private static byte[] Except(byte fc, byte code) => [(byte)(fc | 0x80), code];

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        lock (_clients)
        {
            foreach (var c in _clients) { try { c.Close(); } catch { } }
            _clients.Clear();
        }
        await ValueTask.CompletedTask;
    }
}
