namespace EvolverCollect.Drivers.Modbus;

using System.IO.Ports;
using System.Net.Sockets;

/// <summary>传输层：一次请求-响应事务（Modbus 严格串行，事务内加锁由驱动保证）。</summary>
internal interface IModbusTransport : IDisposable
{
    Task OpenAsync(CancellationToken ct);
    /// <summary>发送请求帧并返回完整响应帧（已按各封装格式组包/拆包与超时控制）。</summary>
    Task<byte[]> TransactAsync(byte[] request, int timeoutMs, CancellationToken ct);
}

/// <summary>Modbus TCP（MBAP 封装）：按 MBAP 长度字段读取完整响应。</summary>
internal class TcpTransport : IModbusTransport
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    protected NetworkStream? Stream;

    public TcpTransport(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public virtual async Task OpenAsync(CancellationToken ct)
    {
        _client?.Dispose();
        _client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await _client.ConnectAsync(_host, _port, timeout.Token);
        Stream = _client.GetStream();
    }

    public virtual async Task<byte[]> TransactAsync(byte[] request, int timeoutMs, CancellationToken ct)
    {
        var stream = Stream ?? throw new IOException("Modbus TCP 未连接");

        await stream.WriteAsync(request, ct);
        await stream.FlushAsync(ct);

        // 读 MBAP 头（7 字节）→ 按 len 字段读余量
        var header = new byte[7];
        await ReadExactlyAsync(stream, header, ct, timeoutMs);
        var len = (header[4] << 8) | header[5];
        if (len < 2 || len > 300) throw new IOException($"Modbus TCP 响应长度字段异常: {len}");
        var rest = new byte[len - 1];
        await ReadExactlyAsync(stream, rest, ct, timeoutMs);
        var frame = new byte[7 + rest.Length];
        header.CopyTo(frame, 0);
        rest.CopyTo(frame, 7);
        return frame;
    }

    protected static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken ct, int timeoutMs)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(200, timeoutMs)));
            try
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token);
                if (n == 0) throw new IOException("连接已被对端关闭");
                read += n;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException($"等待 Modbus 响应超时（{timeoutMs}ms）");
            }
        }
    }

    public virtual void Dispose()
    {
        Stream?.Dispose();
        _client?.Dispose();
    }
}

/// <summary>Modbus RTU over TCP：串口帧格式走网口（常见于串口服务器，无设备测试/教学也用它）。</summary>
internal sealed class RtuOverTcpTransport(string host, int port) : TcpTransport(host, port)
{
    public override Task<byte[]> TransactAsync(byte[] request, int timeoutMs, CancellationToken ct)
    {
        var stream = Stream ?? throw new IOException("RTU-over-TCP 未连接");
        return RtuTransactAsync(stream, request, timeoutMs, ct);
    }

    internal static async Task<byte[]> RtuTransactAsync(Stream stream, byte[] request, int timeoutMs, CancellationToken ct)
    {
        await stream.WriteAsync(request, ct);
        await stream.FlushAsync(ct);
        return await RtuFraming.ReadResponse(stream, request[1], timeoutMs, ct);
    }
}

/// <summary>RTU 串口（RS232/RS485）。帧定界依赖 3.5 字符静默间隔，实现上按推算长度读取。</summary>
internal sealed class SerialRtuTransport : IModbusTransport
{
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly Parity _parity;
    private readonly int _dataBits;
    private readonly StopBits _stopBits;
    private SerialPort? _serial;

    public SerialRtuTransport(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits)
    {
        _portName = portName;
        _baudRate = baudRate;
        _parity = parity;
        _dataBits = dataBits;
        _stopBits = stopBits;
    }

    public Task OpenAsync(CancellationToken ct)
    {
        _serial?.Dispose();
        _serial = new SerialPort(_portName, _baudRate, _parity, _dataBits, _stopBits)
        {
            ReadTimeout = 5000,
            WriteTimeout = 5000,
            ReadBufferSize = 4096,
        };
        _serial.Open();
        _serial.DiscardInBuffer();
        return Task.CompletedTask;
    }

    public async Task<byte[]> TransactAsync(byte[] request, int timeoutMs, CancellationToken ct)
    {
        var serial = _serial ?? throw new IOException("串口未打开");
        serial.WriteTimeout = Math.Max(500, timeoutMs);
        serial.ReadTimeout = Math.Max(500, timeoutMs);
        serial.DiscardInBuffer();
        await serial.BaseStream.WriteAsync(request, ct);
        await serial.BaseStream.FlushAsync(ct);
        return await RtuFraming.ReadResponse(serial.BaseStream, request[1], timeoutMs, ct);
    }

    public void Dispose() => _serial?.Dispose();
}

/// <summary>RTU 响应帧读取：按功能码推算长度（读响应带字节计数字段，写回显固定 8 字节）。</summary>
public static class RtuFraming
{
    public static async Task<byte[]> ReadResponse(Stream stream, byte requestFc, int timeoutMs, CancellationToken ct)
    {
        var buffer = new byte[300];
        var read = 0;
        var deadline = Environment.TickCount64 + timeoutMs + 500;

        while (read < buffer.Length)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) throw new IOException($"等待 Modbus RTU 响应超时（{timeoutMs}ms）");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter((int)Math.Min(remaining, 500));
            int n;
            try { n = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }
            if (n == 0) throw new IOException("串口/链路已关闭");
            read += n;

            var complete = ExpectedLength(read > 1 ? (byte?)buffer[1] : null, buffer, read);
            if (complete is int len && read >= len)
                return buffer[..len];
        }
        throw new IOException("Modbus RTU 响应帧过长");
    }

    /// <summary>已收数据是否足够判断完整帧；返回期望总长度或 null（数据不足）。</summary>
    private static int? ExpectedLength(byte? fc, byte[] b, int read)
    {
        if (fc is null) return read >= 1 ? null : null;
        if ((fc & 0x80) != 0) return 5;                                  // 异常响应：addr+fc+code+crc
        var f = fc.Value;
        if (f is ModbusCodec.ReadCoils or ModbusCodec.ReadDiscreteInputs
               or ModbusCodec.ReadHoldingRegisters or ModbusCodec.ReadInputRegisters)
            return read >= 3 ? 3 + b[2] + 2 : null;                      // addr+fc+count+data+crc
        if (f is ModbusCodec.WriteSingleCoil or ModbusCodec.WriteSingleRegister) return 8; // 回显
        if (f == ModbusCodec.WriteMultipleRegisters) return 8;
        return null;
    }
}
