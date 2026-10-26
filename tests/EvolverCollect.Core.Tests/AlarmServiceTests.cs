namespace EvolverCollect.Core.Tests;

using EvolverCollect.Core.Abstractions;
using EvolverCollect.Core.Alarm;
using EvolverCollect.Core.Domain;
using EvolverCollect.Core.Pipeline;

public class AlarmServiceTests : IAsyncDisposable
{
    private readonly DataHub _hub = new();

    private static AlarmRuleConfig Rule(long id, long tagId, string condition = "Gt", string threshold = "30",
        int duration = 1, int recover = 1) =>
        new(id, $"规则{id}", tagId, condition, threshold, 2, duration, recover, true);

    private static DataPoint P(long tagId, object value) =>
        new(1, 1, tagId, "CNC-01", $"t{tagId}", value, Quality.Good, 1000);

    private static async Task PublishAsync(DataHub hub, params DataPoint[] points)
    {
        hub.Publish(points);
        await Task.Delay(120);
    }

    [Fact]
    public async Task Threshold_alarm_triggers_then_recovers_with_debounce()
    {
        var events = new List<AlarmEvent>();
        var service = new AlarmService(_hub);
        service.Start([Rule(1, 10, duration: 2, recover: 2)]);
        service.AlarmRaised += events.Add;

        await PublishAsync(_hub, P(10, 31));          // 第 1 次满足（防抖 2 → 不触发）
        Assert.Empty(events);

        await PublishAsync(_hub, P(10, 35));          // 第 2 次满足 → 触发
        var raised = Assert.Single(events);
        Assert.False(raised.Recovered);
        Assert.Contains("35", raised.TriggerValue);

        await PublishAsync(_hub, P(10, 25));          // 第 1 次不满足（恢复防抖 2 → 不恢复）
        Assert.Single(events);

        await PublishAsync(_hub, P(10, 20));          // 第 2 次不满足 → 恢复
        Assert.Equal(2, events.Count);
        Assert.True(events[1].Recovered);
    }

    [Fact]
    public async Task Bad_quality_does_not_evaluate()
    {
        var events = new List<AlarmEvent>();
        var service = new AlarmService(_hub);
        service.Start([Rule(1, 10)]);
        service.AlarmRaised += events.Add;

        _hub.Publish([P(10, "断链") with { Quality = Quality.Bad }]);
        await Task.Delay(150);

        Assert.Empty(events); // 断链不参与报警评估
    }

    [Fact]
    public async Task Change_condition_fires_on_value_change()
    {
        var events = new List<AlarmEvent>();
        var service = new AlarmService(_hub);
        service.Start([Rule(1, 10, condition: "Change")]);
        service.AlarmRaised += events.Add;

        await PublishAsync(_hub, P(10, 1));   // 首值建立基准（无上一值，不触发）
        await PublishAsync(_hub, P(10, 1));   // 未变化
        Assert.Empty(events);
        await PublishAsync(_hub, P(10, 2));   // 变化 → 触发
        Assert.Single(events);
    }

    public async ValueTask DisposeAsync() => await _hub.DisposeAsync();
}
