namespace EvolverCollect.Core.Tests;

using EvolverCollect.Core.Abstractions;
using EvolverCollect.Core.Pipeline;

public class DataHubTests : IDisposable
{
    private readonly DataHub _hub = new(capacity: 50);

    [Fact]
    public async Task Subscriber_receives_published_batches_in_order()
    {
        var received = new List<int>();
        _hub.Subscribe(b => { received.AddRange(b.Select(p => (int)p.Value!)); });

        for (var i = 0; i < 10; i++)
            _hub.Publish([new DataPoint(1, 1, i, "d", $"t{i}", i, Quality.Good, 0)]);

        await _hub.DisposeAsync();
        Assert.Equal(Enumerable.Range(0, 10), received);
    }

    [Fact]
    public async Task Slow_subscriber_causes_backpressure_drops()
    {
        var received = 0;
        _hub.Subscribe(_ => { received++; Thread.Sleep(20); });

        // 消费端每批 20ms，一次性灌入 300 批 → 容量 1000 必然溢出 → 丢弃计数
        var published = 0;
        for (var i = 0; i < 300; i++)
        {
            var points = Enumerable.Range(0, 20)
                .Select(j => new DataPoint(1, 1, i * 20 + j, "d", $"t{i}-{j}", j, Quality.Good, 0)).ToList();
            if (_hub.Publish(points)) published += points.Count;
        }

        await _hub.DisposeAsync();
        Assert.True(published > 0);
        Assert.True(_hub.DroppedPoints > 0, $"期望发生背压丢弃，实际 dropped={_hub.DroppedPoints}");
    }

    [Fact]
    public async Task Subscriber_exception_is_isolated()
    {
        var ok = false;
        _hub.Subscribe(_ => throw new InvalidOperationException("订阅者崩了"));
        _hub.Subscribe(_ => ok = true);

        _hub.Publish([new DataPoint(1, 1, 1, "d", "t", 1, Quality.Good, 0)]);
        await _hub.DisposeAsync();
        Assert.True(ok);
    }

    [Fact]
    public void Realtime_cache_keeps_latest_per_tag()
    {
        var cache = new RealtimeCache();
        var p1 = new DataPoint(1, 1, 1, "d", "t1", 1, Quality.Good, 100);
        var p2 = new DataPoint(1, 1, 1, "d", "t1", 2, Quality.Good, 200);
        cache.Update([p1, p2]);
        Assert.Equal(2, cache.Get(1)!.Value);
        Assert.Single(cache.Snapshot());
        cache.RemoveDevice(1);
        Assert.Null(cache.Get(1));
    }

    public void Dispose() => _hub.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
