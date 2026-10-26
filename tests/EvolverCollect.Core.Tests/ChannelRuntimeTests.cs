namespace EvolverCollect.Core.Tests;

using System.Collections.Concurrent;
using EvolverCollect.Core.Abstractions;
using EvolverCollect.Core.Domain;
using EvolverCollect.Core.Pipeline;
using EvolverCollect.Core.Runtime;

/// <summary>可控的假驱动：模拟连接成功/失败、读取成功/抛异常，验证运行时的重连与 Bad 通告行为。</summary>
public sealed class FakeDriver : IDriver
{
    public bool ConnectShouldFail;
    public volatile bool ReadShouldThrow;
    public object? NextValue = 42;

    public DriverDescriptor Descriptor { get; } = new("fake", "假驱动", "test", "{}", DriverCapabilities.Read);
    public DriverStats Stats { get; } = new();

    public Task<ConnectResult> ConnectAsync(CancellationToken ct) =>
        Task.FromResult(ConnectShouldFail ? ConnectResult.Fail("模拟连接失败") : ConnectResult.Ok());

    public Task DisconnectAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public ITagBrowser? CreateTagBrowser() => null;

    public Task<IReadOnlyList<DataPoint>> ReadAsync(DeviceBatch batch, CancellationToken ct)
    {
        if (ReadShouldThrow) throw new IOException("模拟链路中断");
        var value = System.Threading.Interlocked.Increment(ref _counter);
        IReadOnlyList<DataPoint> points = [.. batch.Tags.Select(t =>
            new DataPoint(batch.ChannelId, batch.DeviceId, t.Id, batch.DeviceName, t.Name, value, Quality.Good, 1000))];
        return Task.FromResult(points);
    }

    private long _counter;

    public Task<WriteResult> WriteAsync(TagDefinition tag, object value, string deviceName, int? stationNo, CancellationToken ct) =>
        Task.FromResult(WriteResult.Ok());
}

public sealed class FakeDriverFactory : IDriverFactory
{
    public FakeDriver Driver { get; } = new();
    public string DriverId => "fake";
    public DriverDescriptor Descriptor { get; } = new("fake", "假驱动", "test", "{}", DriverCapabilities.Read);
    public IDriver Create(string connectionJson) => Driver;
}

public class ChannelRuntimeTests : IAsyncDisposable
{
    private readonly DataHub _hub = new();
    private readonly ConcurrentQueue<IReadOnlyList<DataPoint>> _published = new();

    private static ChannelSnapshot Snapshot() => new(
        new ChannelConfig(1, "测试通道", "fake", true, "{}", 200, 3000, 2, null),
        [new DeviceConfig(1, 1, "DEV-1", 1, null, true, null)],
        [new TagDefinition(1, 1, "t1", "40001", TagDataType.Int16, false, null, 1, 0, null, null, null)]);

    private ChannelRuntime Start(FakeDriverFactory factory)
    {
        var runtime = new ChannelRuntime(Snapshot(), factory, _hub, null);
        _hub.Subscribe(_published.Enqueue);
        return runtime;
    }

    [Fact]
    public async Task Initial_connect_failure_goes_degraded_without_bad_points()
    {
        var factory = new FakeDriverFactory();
        factory.Driver.ConnectShouldFail = true;
        var runtime = new ChannelRuntime(Snapshot(), factory, _hub, null);
        await runtime.StartAsync();

        Assert.Equal(ChannelState.Degraded, runtime.State);
        Assert.False(runtime.Connected);
        Assert.Empty(_published); // 从未连上：不发 Bad（界面看通道状态即可）
        await runtime.StopAsync();
    }

    [Fact]
    public async Task Read_failure_publishes_bad_once_then_reconnects()
    {
        var factory = new FakeDriverFactory();
        var runtime = new ChannelRuntime(Snapshot(), factory, _hub, null);
        _hub.Subscribe(_published.Enqueue);

        await runtime.StartAsync();
        await Task.Delay(600); // 等第一轮采集
        Assert.Equal(ChannelState.Running, runtime.State);
        Assert.True(_published.Count >= 1);
        Assert.All(_published.SelectMany(b => b), p => Assert.Equal(Quality.Good, p.Quality));

        // 链路中断：整批 Bad 通告一次 + 进入 Degraded
        factory.Driver.ReadShouldThrow = true;
        await Task.Delay(700);
        Assert.Equal(ChannelState.Degraded, runtime.State);
        Assert.Contains(_published, b => b.Any(p => p.Quality == Quality.Bad));

        // 恢复：指数退避第 0 次为 1s，重连成功后回到 Running 并继续出数
        factory.Driver.ReadShouldThrow = false;
        await Task.Delay(2500);
        Assert.Equal(ChannelState.Running, runtime.State);
        Assert.True(_published.Count >= 3);

        await runtime.StopAsync();
        Assert.Equal(ChannelState.Stopped, runtime.State);
    }

    [Fact]
    public async Task Effective_interval_respects_tag_override()
    {
        // 点位覆盖 200ms 周期：1.5s 内应产生 >=5 个批次
        var snapshot = Snapshot() with
        {
            Tags = [Snapshot().Tags[0] with { PollIntervalMs = 200 }],
        };
        var factory = new FakeDriverFactory();
        var runtime = new ChannelRuntime(snapshot, factory, _hub, null);
        _hub.Subscribe(_published.Enqueue);

        await runtime.StartAsync();
        await Task.Delay(1500);
        await runtime.StopAsync();

        Assert.True(_published.Count >= 5, $"1.5s 内仅采集 {_published.Count} 批");
    }

    public async ValueTask DisposeAsync() => await _hub.DisposeAsync();
}
