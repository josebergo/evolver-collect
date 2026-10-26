namespace EvolverCollect.Core.Tests;

using System.Collections.Concurrent;
using EvolverCollect.Core.Abstractions;
using EvolverCollect.Core.Domain;
using EvolverCollect.Core.Pipeline;

/// <summary>内存版离线队列（测试替身）。</summary>
public sealed class InMemorySpoolStore : ISpoolStore
{
    private readonly ConcurrentQueue<SpoolItem> _items = new();
    private long _nextId = 1;

    public List<SpoolItem> AllItems => [.. _items];

    public Task EnqueueAsync(long northAppId, string payload, CancellationToken ct = default)
    {
        _items.Enqueue(new SpoolItem(_nextId++, northAppId, payload, DateTime.UtcNow));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SpoolItem>> DequeueAsync(long northAppId, int max, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SpoolItem>>([.. _items.Where(i => i.NorthAppId == northAppId).OrderBy(i => i.Id).Take(max)]);

    public Task MarkSentAsync(long northAppId, long upToId, CancellationToken ct = default)
    {
        var remaining = new Queue<SpoolItem>();
        foreach (var item in _items)
            if (!(item.NorthAppId == northAppId && item.Id <= upToId))
                remaining.Enqueue(item);
        _items.Clear();
        foreach (var item in remaining) _items.Enqueue(item);
        return Task.CompletedTask;
    }

    public Task TrimAsync(long northAppId, long maxRows, CancellationToken ct = default)
    {
        var keep = _items.Where(i => i.NorthAppId == northAppId).OrderByDescending(i => i.Id).Take((int)maxRows).Select(i => i.Id).ToHashSet();
        var remaining = new Queue<SpoolItem>();
        foreach (var item in _items)
            if (item.NorthAppId != northAppId || keep.Contains(item.Id))
                remaining.Enqueue(item);
        _items.Clear();
        foreach (var item in remaining) _items.Enqueue(item);
        return Task.CompletedTask;
    }

    public Task<long> GetDepthAsync(long northAppId, CancellationToken ct = default) =>
        Task.FromResult((long)_items.Count(i => i.NorthAppId == northAppId));

    public Task<long> GetTotalDepthAsync(CancellationToken ct = default) =>
        Task.FromResult((long)_items.Count);
}

/// <summary>可控的假北向出口：验证断点续传（失败转存 → 重连 → 回放）。</summary>
public sealed class FakeNorthSink : INorthboundSink
{
    public volatile bool ShouldFail;
    public volatile bool Connected;
    public List<string> SentPayloads { get; } = [];
    public List<string> ReplayedPayloads { get; } = [];

    public string Type => "fake";

    public Task<ConnectResult> ConnectAsync(CancellationToken ct)
    {
        Connected = true;
        return Task.FromResult(ConnectResult.Ok());
    }

    public Task DisconnectAsync() { Connected = false; return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task<bool> SendAsync(IReadOnlyList<DataPoint> points, string formattedPayload, CancellationToken ct)
    {
        if (ShouldFail || !Connected) return Task.FromResult(false);
        lock (SentPayloads) SentPayloads.Add(formattedPayload);
        return Task.FromResult(true);
    }

    public Task<bool> SendRawAsync(string payload, CancellationToken ct)
    {
        if (ShouldFail || !Connected) return Task.FromResult(false);
        lock (ReplayedPayloads) ReplayedPayloads.Add(payload);
        return Task.FromResult(true);
    }
}

public sealed class FakeNorthFactory(FakeNorthSink sink) : INorthboundFactory
{
    public string Type => "fake";
    public NorthboundDescriptor Descriptor { get; } = new("fake", "假北向", "{}");
    public INorthboundSink Create(string connectionJson, NorthAppOptions options) => sink;
}

public class NorthAppRuntimeTests : IAsyncDisposable
{
    private readonly DataHub _hub = new();

    private static NorthAppConfig Config(string? include = null) => new(
        7, "测试北向", "fake", true, "{}",
        new NorthAppOptions(include, null, "json-lines", 100, 50));

    private static DataPoint Point(long id, string device = "DEV-1") =>
        new(1, 1, id, device, $"t{id}", id, Quality.Good, 1000);

    private async Task PublishAsync(params DataPoint[] points)
    {
        _hub.Publish(points);
        await Task.Delay(120); // 凑批窗口 50ms + 发送
    }

    [Fact]
    public async Task Points_matching_filter_are_sent()
    {
        var sink = new FakeNorthSink();
        var runtime = new NorthAppRuntime(Config("DEV-1.*"), new FakeNorthFactory(sink), _hub, new InMemorySpoolStore());
        await runtime.StartAsync();

        await PublishAsync(Point(1), Point(2));
        await PublishAsync(Point(3, "DEV-2")); // 不匹配 include

        lock (sink.SentPayloads)
        {
            var all = string.Join("", sink.SentPayloads);
            Assert.Contains("\"tag\":\"t1\"", all);
            Assert.Contains("\"tag\":\"t2\"", all);
            Assert.DoesNotContain("\"tag\":\"t3\"", all);
        }

        await runtime.StopAsync();
    }

    [Fact]
    public async Task Send_failure_spools_then_replays_after_reconnect()
    {
        var sink = new FakeNorthSink { ShouldFail = true };
        var spool = new InMemorySpoolStore();
        var runtime = new NorthAppRuntime(Config(), new FakeNorthFactory(sink), _hub, spool)
        {
            ReconnectDelayMs = 100,
            ReplayPollMs = 100,
        };
        await runtime.StartAsync();

        await PublishAsync(Point(1), Point(2));
        Assert.True(spool.AllItems.Count >= 1, "发送失败应落入离线队列");
        var spooledLines = spool.AllItems.Sum(p => p.Payload.Split((char)10).Length);
        Assert.Equal(2, spooledLines);

        // 恢复链路 → 回放积压 → 水位推进清空
        sink.ShouldFail = false;
        await Task.Delay(1500);

        Assert.NotEmpty(sink.ReplayedPayloads);
        Assert.Empty(spool.AllItems);

        var status = runtime.GetStatus();
        Assert.True(status.Connected);
        Assert.True(status.ReplayedPoints >= 1, $"回放批次数 {status.ReplayedPoints}");

        await runtime.StopAsync();
    }

    public async ValueTask DisposeAsync() => await _hub.DisposeAsync();
}
