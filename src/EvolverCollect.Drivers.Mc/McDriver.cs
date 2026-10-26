namespace EvolverCollect.Drivers.Mc;

using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using EvolverCollect.Core.Abstractions;

/// <summary>三菱 MC（MELSEC）Q/L/R 系列以太网协议，3E 帧（Binary），端口 5000。</summary>
public sealed class McDriver(McOptions options) : IDriver
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private byte _sid;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    public DriverDescriptor Descriptor => McDriverFactory.DescriptorValue;
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

            // 探测读 D0 一个字（连接即通，无需握手；3E 帧直接收发）
            try
            {
                await _ioLock.WaitAsync(ct);
                try { await TransactWordReadAsync(McDevice.D, 0, 1, ct); }
                finally { _ioLock.Release(); }
            }
            catch (McException) { /* 设备应答异常码 = 链路通 */ }
            return ConnectResult.Ok();
        }
        catch (Exception ex)
        {
            return ConnectResult.Fail($"MC 连接失败: {ex.Message}");
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
        var parsed = new List<(TagDefinition Tag, McAddress Addr)>();

        foreach (var tag in batch.Tags)
        {
            try { parsed.Add((tag, McAddress.Parse(tag.Address))); }
            catch (FormatException ex)
            {
                points.Add(DataPoint.BadPoint(batch.ChannelId, batch.DeviceId, tag.Id, batch.DeviceName, tag.Name, ex.Message, ts));
            }
        }

        // 位软元件：按设备组批量位读（每点 1 字节应答）
        foreach (var group in parsed.Where(p => p.Addr.IsBit).GroupBy(p => p.Addr.Device))
        {
            var items = group.OrderBy(a => a.Addr.Offset).ToList();
            try
            {
                await _ioLock.WaitAsync(ct);
                bool[] bits;
                try
                {
                    var request = McFrame.BuildBitRead((byte)group.Key, items[0].Addr.Offset, (ushort)items.Count, ++_sid);
                    var response = await TransactAsync(request, ct);
                    bits = McFrame.ParseBitReadResponse(response, (ushort)items.Count);
                }
                finally { _ioLock.Release(); }

                for (var i = 0; i < items.Count; i++)
                {
                    var (tag, _) = items[i];
                    var value = tag.DataType == TagDataType.Bool ? bits[i] : BadType(tag);
                    points.Add(value is string reason
                        ? DataPoint.BadPoint(batch.ChannelId, batch.DeviceId, tag.Id, batch.DeviceName, tag.Name, reason, ts)
                        : new DataPoint(batch.ChannelId, batch.DeviceId, tag.Id, batch.DeviceName, tag.Name, value, Quality.Good, ts));
                }
                Stats.RecordRead(default, true, items.Count);
            }
            catch (McException mex)
            {
                Stats.RecordRead(default, false, items.Count, mex.Message);
                foreach (var (tag, _) in items)
                    points.Add(DataPoint.BadPoint(batch.ChannelId, batch.DeviceId, tag.Id, batch.DeviceName, tag.Name, mex.Message, ts));
            }
        }

        // 字软元件：按设备组批量字读（相邻点位一次读完）
        foreach (var group in parsed.Where(p => !p.Addr.IsBit).GroupBy(p => p.Addr.Device))
        {
            var items = group.OrderBy(a => a.Addr.Offset).ToList();
            var start = items[0].Addr.Offset;
            var wordCount = items.Max(a => a.Addr.Offset - start + McValues.WordCount(a.Tag.DataType));
            try
            {
                ushort[] words;
                await _ioLock.WaitAsync(ct);
                try { words = await TransactWordReadAsync(group.Key, start, (ushort)Math.Min(wordCount, McFrame.MaxWordsPerRead), ct); }
                finally { _ioLock.Release(); }

                foreach (var (tag, addr) in items)
                {
                    try
                    {
                        var value = tag.DataType == TagDataType.Bool
                            ? words[addr.Offset - start]   // 字拆位交给点位引擎 BitIndex
                            : McValues.DecodeWords(tag.DataType, words, addr.Offset - start);
                        points.Add(new DataPoint(batch.ChannelId, batch.DeviceId, tag.Id, batch.DeviceName, tag.Name, value, Quality.Good, ts));
                    }
                    catch (Exception ex)
                    {
                        points.Add(DataPoint.BadPoint(batch.ChannelId, batch.DeviceId, tag.Id, batch.DeviceName, tag.Name, ex.Message, ts));
                    }
                }
                Stats.RecordRead(default, true, items.Count);
            }
            catch (McException mex)
            {
                Stats.RecordRead(default, false, items.Count, mex.Message);
                foreach (var (tag, _) in items)
                    points.Add(DataPoint.BadPoint(batch.ChannelId, batch.DeviceId, tag.Id, batch.DeviceName, tag.Name, mex.Message, ts));
            }
        }
        return points;
    }

    private static object BadType(TagDefinition tag) =>
        tag.DataType == TagDataType.Bool ? true : $"位软元件仅支持 Bool（当前 {tag.DataType}）";

    public async Task<WriteResult> WriteAsync(TagDefinition tag, object value, string deviceName, int? stationNo, CancellationToken ct)
    {
        McAddress addr;
        try { addr = McAddress.Parse(tag.Address); }
        catch (FormatException ex) { return WriteResult.Fail(ex.Message); }
        if (!tag.Writable) return WriteResult.Fail("点位未开启可写");

        try
        {
            await _ioLock.WaitAsync(ct);
            try
            {
                if (addr.IsBit)
                {
                    var on = Convert.ToBoolean(value);
                    await TransactBitWriteAsync(addr.Device, addr.Offset, [on], ct);
                }
                else
                {
                    var words = McValues.EncodeWords(tag.DataType, value);
                    await TransactWordWriteAsync(addr.Device, addr.Offset, words, ct);
                }
                return WriteResult.Ok();
            }
            finally { _ioLock.Release(); }
        }
        catch (McException mex) { return WriteResult.Fail(mex.Message); }
        catch (Exception ex) { return WriteResult.Fail(ex.Message); }
    }

    // ---------- 传输与帧 ----------

    private async Task<ushort[]> TransactWordReadAsync(McDevice device, int offset, ushort count, CancellationToken ct)
    {
        var request = McFrame.BuildWordRead((byte)device, offset, count, ++_sid);
        var response = await TransactAsync(request, ct);
        return McFrame.ParseWordReadResponse(response, count);
    }

    private async Task TransactWordWriteAsync(McDevice device, int offset, ushort[] words, CancellationToken ct)
    {
        var request = McFrame.BuildWordWrite((byte)device, offset, words, ++_sid);
        var response = await TransactAsync(request, ct);
        McFrame.ParseWriteResponse(response);
    }

    private async Task TransactBitWriteAsync(McDevice device, int offset, bool[] bits, CancellationToken ct)
    {
        var request = McFrame.BuildBitWrite((byte)device, offset, bits, ++_sid);
        var response = await TransactAsync(request, ct);
        McFrame.ParseWriteResponse(response);
    }

    private async Task<byte[]> TransactAsync(byte[] request, CancellationToken ct)
    {
        var stream = _stream ?? throw new IOException("MC 未连接");
        await stream.WriteAsync(request, ct);
        // 响应头 15 字节（含长度），按长度读余量
        var header = new byte[15];
        await ReadExactlyAsync(stream, header, ct, options.TimeoutMs);
        var len = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(9));
        var rest = new byte[len - 2];
        await ReadExactlyAsync(stream, rest, ct, options.TimeoutMs);
        return [.. header, .. rest];
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken ct, int timeoutMs)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Math.Max(200, timeoutMs));
            try
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token);
                if (n == 0) throw new IOException("连接已关闭");
                read += n;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException($"等待 MC 响应超时（{timeoutMs}ms）");
            }
        }
    }
}

public sealed class McOptions
{
    public string Host { get; set; } = "192.168.1.10";
    public int Port { get; set; } = 5000;
    public int TimeoutMs { get; set; } = 3000;
}

/// <summary>MC 异常（从站应答非 0 结束码）。</summary>
public sealed class McException(string message, ushort endCode) : Exception(message)
{
    public ushort EndCode { get; } = endCode;
}
