namespace EvolverCollect.Core.Engine;

using System.Collections.Concurrent;
using EvolverCollect.Core.Abstractions;

/// <summary>
/// 点位引擎：原始值 → 工程量。
/// 职责：位拆取（寄存器某一位当开关量）、线性换算 value = a*x + b、死区过滤（值没变就不重复发布）。
/// 有状态（每个采集运行时持有独立实例）：首值必发、质量变化必发、越死区必发。
/// </summary>
public sealed class TagEngine
{
    private sealed class TagState
    {
        public object? LastValue;
        public bool EverEmitted;
        public Quality LastQuality = Quality.Bad;
    }

    private readonly bool _emitBad;
    private readonly ConcurrentDictionary<long, TagState> _states = new();

    /// <param name="emitBadPoints">断连时是否仍发布 Bad 点位（默认发布，让北向知道数据断了）。</param>
    public TagEngine(bool emitBadPoints = true) => _emitBad = emitBadPoints;

    public IReadOnlyList<DataPoint> Process(DeviceBatch batch, IReadOnlyList<DataPoint> raw)
    {
        if (raw.Count == 0) return [];

        var byTag = new Dictionary<long, DataPoint>(raw.Count);
        foreach (var p in raw)
            if (!byTag.ContainsKey(p.TagId)) byTag[p.TagId] = p;

        var output = new List<DataPoint>(raw.Count);
        foreach (var tag in batch.Tags)
        {
            if (!byTag.TryGetValue(tag.Id, out var point)) continue;
            var processed = ProcessOne(tag, point);
            if (processed is not null) output.Add(processed);
        }
        return output;
    }

    private DataPoint? ProcessOne(TagDefinition tag, DataPoint point)
    {
        var state = _states.GetOrAdd(tag.Id, _ => new TagState());

        if (point.Quality == Quality.Bad)
        {
            var firstEver = !state.EverEmitted;
            var alreadyBad = state.EverEmitted && state.LastQuality == Quality.Bad;
            state.EverEmitted = true;
            state.LastQuality = Quality.Bad;
            state.LastValue = null;
            // 只在 Good→Bad 跃迁（或配置了上报且是该点首个值）时发布，持续 Bad 不重复刷屏
            return !alreadyBad || (firstEver && _emitBad) ? point : null;
        }

        object? value = point.Value;

        // 1. 位拆取：把整数寄存器的某一位取出来当开关量
        if (tag.BitIndex is int bit and >= 0 and < 63 && value is not null and not bool and not string)
        {
            try { value = (Convert.ToUInt64(value) & (1UL << bit)) != 0; }
            catch (OverflowException) { /* 非整数无法取位，保持原值 */ }
        }

        // 2. 线性换算（仅数值型且配置了非默认系数）
        if (value is not null && IsNumeric(value) && (tag.ScaleA != 1.0 || tag.ScaleB != 0.0))
        {
            value = tag.ScaleA * Convert.ToDouble(value) + tag.ScaleB;
        }

        // 3. 死区/变化过滤：对比基准是“上一次发布的值”（被抑制的中间值不更新基准）
        bool emit;
        if (!state.EverEmitted) emit = true;
        else if (value is bool b && state.LastValue is bool lb) emit = b != lb;
        else if (IsNumeric(value) && state.LastValue is not null && IsNumeric(state.LastValue))
        {
            var delta = Math.Abs(Convert.ToDouble(value) - Convert.ToDouble(state.LastValue));
            emit = tag.Deadband is double db ? delta > db : delta > 0;
        }
        else emit = !Equals(value, state.LastValue);

        state.EverEmitted = true;
        state.LastQuality = point.Quality;
        if (!emit) return null;
        state.LastValue = value;
        return point with { Value = value };
    }

    private static bool IsNumeric(object? v) =>
        v is int or long or short or sbyte or uint or ulong or ushort or byte or float or double or decimal;
}
