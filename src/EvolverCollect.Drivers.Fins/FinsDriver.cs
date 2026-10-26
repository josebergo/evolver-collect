namespace EvolverCollect.Drivers.Fins;

using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using EvolverCollect.Core.Abstractions;

/// <summary>欧姆龙 FINS/TCP 驱动（CJ/CS/CP/NJ 系列，端口 9600，含节点握手）。</summary>
public sealed class FinsDriver(FinsOptions options) : IDriver
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private byte _serverNode = 1;
    private byte _clientNode = 0;
    private byte _sid;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    public DriverDescriptor Descriptor => FinsDriverFactory.DescriptorValue;
    public DriverStats Stats { get; } = new();

    public async Task<ConnectResult> ConnectAsync(CancellationToken ct)
    {
        try
        {
            _client?.Dispose();
            _client = new TcpClient { NoDelay = true };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await _client.ConnectAsync(options.Host, options.Port, timeout.Token);
            _stream = _client.GetStream();

            // FINS/TCP 节点握手：告知客户端节点号，PLC 回它的节点号
            _clientNode = (byte)(options.ClientNode ?? 254);
            var handshake = new byte[]
            {
                0x46, 0x49, 0x4E, 0x53, 0, 0, 0, 0x0C, 0, 0, 0, 0, 0, 0, 0, 0, _clientNode, 0, 0, 0,
            };
            await _stream.WriteAsync(handshake, ct);
            var header = new byte[16];
            await ReadExactlyAsync(header, ct);
            _serverNode = (byte)(options.ServerNode ?? header[15]);

            // 探测读 D0 一个字
            try
            {
                await _ioLock.WaitAsync(ct);
                try { await TransactReadAsync(FinsArea.DmWord, 0, 1, ct); }
                finally { _ioLock.Release(); }
            }
            catch (FinsException) { /* 有应答即链路通 */ }
            return ConnectResult.Ok();
        }
        catch (Exception ex)
        {
            return ConnectResult.Fail($"FINS 连接失败: {ex.Message}");
        }
    }

    public Task DisconnectAsync()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
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
        var ts = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var points = new List<DataPoint>(batch.Tags.Count);
        var parsed = new List<(TagDefinition Tag, FinsAddress Addr)>();

        foreach (var tag in batch.Tags)
        {
            try { parsed.Add((tag, FinsAddress.Parse(tag.Address))); }
            catch (FormatException ex)
            {
                points.Add(DataPoint.BadPoint(batch.ChannelId, batch.DeviceId, tag.Id, batch.DeviceName, tag.Name, ex.Message, ts));
            }
        }

        foreach (var group in parsed.GroupBy(p => p.Addr.Area))
        {
            var items = group.OrderBy(a => a.Addr.BitAddress).ToList();
            var isBitArea = group.Key.IsBitArea();
            var start = isBitArea ? items[0].Addr.BitAddress : items[0].Addr.Offset;
            var count = isBitArea
                ? items.Count
                : items.Max(a => a.Addr.Offset - start + FinsValues.WordCount(a.Tag.DataType));

            try
            {
                byte[] data;
                await _ioLock.WaitAsync(ct);
                try { data = await TransactReadAsync(group.Key, start, (ushort)count, ct); }
                finally { _ioLock.Release(); }

                foreach (var (tag, addr) in items)
                {
                    try
                    {
                        object value;
                        if (isBitArea)
                        {
                            var bitIndex = addr.BitAddress - start;
                            value = tag.DataType == TagDataType.Bool
                                ? (data[bitIndex / 8] & (1 << (bitIndex % 8))) != 0
                                : $"位区仅支持 Bool（当前 {tag.DataType}）";
                        }
                        else if (tag.DataType == TagDataType.Bool)
                        {
                            value = data[(addr.Offset - start) * 2] << 8 | data[(addr.Offset - start) * 2 + 1];
                        }
                        else
                        {
                            value = FinsValues.Decode(tag.DataType, data, (addr.Offset - start) * 2);
                        }
                        points.Add(value is string reason
                            ? DataPoint.BadPoint(batch.ChannelId, batch.DeviceId, tag.Id, batch.DeviceName, tag.Name, reason, ts)
                            : new DataPoint(batch.ChannelId, batch.DeviceId, tag.Id, batch.DeviceName, tag.Name, value, Quality.Good, ts));
                    }
                    catch (Exception ex)
                    {
                        points.Add(DataPoint.BadPoint(batch.ChannelId, batch.DeviceId, tag.Id, batch.DeviceName, tag.Name, ex.Message, ts));
                    }
                }
                Stats.RecordRead(default, true, items.Count);
            }
            catch (FinsException fex)
            {
                Stats.RecordRead(default, false, items.Count, fex.Message);
                foreach (var (tag, _) in items)
                    points.Add(DataPoint.BadPoint(batch.ChannelId, batch.DeviceId, tag.Id, batch.DeviceName, tag.Name, fex.Message, ts));
            }
        }
        return points;
    }

    public async Task<WriteResult> WriteAsync(TagDefinition tag, object value, string deviceName, int? stationNo, CancellationToken ct)
    {
        FinsAddress addr;
        try { addr = FinsAddress.Parse(tag.Address); }
        catch (FormatException ex) { return WriteResult.Fail(ex.Message); }
        if (!tag.Writable) return WriteResult.Fail("点位未开启可写");

        try
        {
            await _ioLock.WaitAsync(ct);
            try
            {
                if (addr.Area.IsBitArea())
                {
                    if (tag.DataType != TagDataType.Bool) return WriteResult.Fail("位区仅支持 Bool");
                    await TransactBitWriteAsync(addr, Convert.ToBoolean(value), ct);
                }
                else
                {
                    var words = FinsValues.Encode(tag.DataType, value);
                    await TransactWordWriteAsync(addr, words, ct);
                }
                return WriteResult.Ok();
            }
            finally { _ioLock.Release(); }
        }
        catch (FinsException fex) { return WriteResult.Fail(fex.Message); }
        catch (Exception ex) { return WriteResult.Fail(ex.Message); }
    }

    // ---------- FINS 帧 ----------

    private async Task<byte[]> TransactReadAsync(FinsArea area, int start, ushort count, CancellationToken ct)
    {
        var fins = FinsFrame.BuildMemoryRead(area, start, count, _serverNode, _clientNode, ++_sid);
        var response = await TransactTcpAsync(fins, ct);
        return FinsFrame.ParseMemoryReadResponse(response, area.IsBitArea() ? (count + 7) / 8 : count * 2);
    }

    private async Task TransactWordWriteAsync(FinsAddress addr, ushort[] words, CancellationToken ct)
    {
        var fins = FinsFrame.BuildMemoryWrite(addr.Area, addr.Offset, words, _serverNode, _clientNode, ++_sid);
        var response = await TransactTcpAsync(fins, ct);
        FinsFrame.ParseWriteResponse(response);
    }

    private async Task TransactBitWriteAsync(FinsAddress addr, bool on, CancellationToken ct)
    {
        // 位写 1 个位：位打包（1 字节）
        var fins = FinsFrame.BuildMemoryWrite(addr.Area, addr.BitAddress, [(ushort)(on ? 1 : 0)], _serverNode, _clientNode, ++_sid);
        var response = await TransactTcpAsync(fins, ct);
        FinsFrame.ParseWriteResponse(response);
    }

    private async Task<byte[]> TransactTcpAsync(byte[] finsFrame, CancellationToken ct)
    {
        var stream = _stream ?? throw new IOException("FINS 未连接");
        var request = new byte[8 + finsFrame.Length];
        "FINS"u8.CopyTo(request);
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(4), finsFrame.Length);
        finsFrame.CopyTo(request, 8);

        await stream.WriteAsync(request, ct);
        var header = new byte[8];
        await ReadExactlyAsync(header, ct);
        var len = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
        if (len is < 2 or > 2048) throw new IOException($"FINS 响应长度异常: {len}");
        var body = new byte[len];
        await ReadExactlyAsync(body, ct);
        return body;
    }

    private async Task ReadExactlyAsync(byte[] buffer, CancellationToken ct)
    {
        var stream = _stream ?? throw new IOException("FINS 未连接");
        var read = 0;
        while (read < buffer.Length)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Math.Max(200, options.TimeoutMs));
            try
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token);
                if (n == 0) throw new IOException("连接已关闭");
                read += n;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException($"等待 FINS 响应超时（{options.TimeoutMs}ms）");
            }
        }
    }
}

public sealed class FinsOptions
{
    public string Host { get; set; } = "192.168.1.5";
    public int Port { get; set; } = 9600;
    public int? ClientNode { get; set; }
    public int? ServerNode { get; set; }
    public int TimeoutMs { get; set; } = 3000;
}

public sealed class FinsException(string message, ushort endCode) : Exception(message)
{
    public ushort EndCode { get; } = endCode;
}
