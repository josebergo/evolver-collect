namespace EvolverCollect.Core.Alarm;

using System.Collections.Concurrent;
using EvolverCollect.Core.Abstractions;
using EvolverCollect.Core.Domain;
using EvolverCollect.Core.Pipeline;

/// <summary>报警事件（界面/SignalR 推送用）。</summary>
public sealed record AlarmEvent(
    long RuleId, string RuleName, long TagId, string TagName, string DeviceName,
    int Level, string Message, bool Recovered, DateTime OccurredAt, string TriggerValue);

/// <summary>
/// 报警引擎：订阅数据流，按规则评估（阈值/变化），带触发防抖与恢复防抖。
/// 状态机：正常 --(连续 N 次满足)--> 报警 --(连续 M 次不满足)--> 恢复。
/// </summary>
public sealed class AlarmService(DataHub hub)
{
    private readonly ConcurrentDictionary<long, RuleRuntime> _rules = new();
    private IDisposable? _subscription;

    /// <summary>报警/恢复事件（界面与 SignalR 订阅）。</summary>
    public event Action<AlarmEvent>? AlarmRaised;
    /// <summary>报警历史落库回调（由宿主接入存储层，保持 Core 无持久化依赖）。</summary>
    public Func<AlarmEvent, long>? HistorySink { get; set; }

    public void Start(IReadOnlyList<AlarmRuleConfig> rules)
    {
        _rules.Clear();
        foreach (var rule in rules.Where(r => r.Enabled))
            _rules[rule.Id] = new RuleRuntime(rule);
        _subscription ??= hub.Subscribe(OnPoints);
    }

    /// <summary>规则变更后热加载。</summary>
    public void Reload(IReadOnlyList<AlarmRuleConfig> rules) => Start(rules);

    public IReadOnlyList<AlarmRuleConfig> Rules => [.. _rules.Values.Select(r => r.Rule)];

    private void OnPoints(IReadOnlyList<DataPoint> points)
    {
        if (_rules.IsEmpty) return;
        foreach (var point in points)
        {
            foreach (var rule in _rules.Values)
            {
                if (rule.Rule.TagId != point.TagId) continue;
                Evaluate(rule, point);
            }
        }
    }

    private void Evaluate(RuleRuntime runtime, DataPoint point)
    {
        var rule = runtime.Rule;
        bool? condition = point.Quality switch
        {
            Quality.Bad => null, // 断链不参与报警评估
            _ => rule.Condition.ToUpperInvariant() switch
            {
                "GT" => IsNumber(point.Value) && Convert.ToDouble(point.Value) > ParseThreshold(rule),
                "LT" => IsNumber(point.Value) && Convert.ToDouble(point.Value) < ParseThreshold(rule),
                "EQ" => Equals(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}", point.Value), rule.Threshold.Trim()),
                "NE" => !Equals(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}", point.Value), rule.Threshold.Trim()),
                "CHANGE" => runtime.LastValue is not null && !Equals(runtime.LastValue, point.Value),
                _ => null,
            },
        };
        var value = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}", point.Value);
        runtime.LastValue = point.Value;

        if (condition is null) return;

        if (condition.Value)
        {
            runtime.FailStreak = 0;
            if (!runtime.Triggered && ++runtime.TriggerStreak >= Math.Max(1, rule.DurationEvaluations))
            {
                runtime.Triggered = true;
                Raise(new AlarmEvent(rule.Id, rule.Name, rule.TagId, point.TagName, point.DeviceName,
                    rule.Level, $"{point.DeviceName}.{point.TagName} = {value} 触发规则（{rule.Condition} {rule.Threshold}）",
                    Recovered: false, OccurredAt: DateTime.Now, TriggerValue: value));
            }
        }
        else
        {
            runtime.TriggerStreak = 0;
            if (runtime.Triggered && ++runtime.FailStreak >= Math.Max(1, rule.RecoverEvaluations))
            {
                runtime.Triggered = false;
                Raise(new AlarmEvent(rule.Id, rule.Name, rule.TagId, point.TagName, point.DeviceName,
                    rule.Level, $"{point.DeviceName}.{point.TagName} 已恢复正常",
                    Recovered: true, OccurredAt: DateTime.Now, TriggerValue: value));
            }
        }
    }

    private void Raise(AlarmEvent alarm)
    {
        try
        {
            if (!alarm.Recovered && HistorySink is not null)
                HistorySink(alarm);
            AlarmRaised?.Invoke(alarm);
        }
        catch { /* 订阅者异常隔离 */ }
    }

    private static double ParseThreshold(AlarmRuleConfig rule) =>
        double.TryParse(rule.Threshold, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 0;

    private static bool IsNumber(object? v) =>
        v is int or long or short or float or double or decimal or uint or ulong or ushort or sbyte or byte;

    private sealed class RuleRuntime(AlarmRuleConfig rule)
    {
        public AlarmRuleConfig Rule { get; } = rule;
        public object? LastValue;
        public bool Triggered;
        public int TriggerStreak;
        public int FailStreak;
    }
}
