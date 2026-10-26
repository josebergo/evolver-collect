namespace EvolverCollect.Core.Pipeline;

using System.Collections.Concurrent;
using EvolverCollect.Core.Abstractions;

/// <summary>
/// 实时值缓存：维护每个点位的最新值快照，供 API / 控制台 / SignalR 读取。
/// 数据量 = 点位数（万级以内），全内存字典足够。
/// </summary>
public sealed class RealtimeCache
{
    private readonly ConcurrentDictionary<long, DataPoint> _latest = new();

    public void Update(IReadOnlyList<DataPoint> points)
    {
        foreach (var p in points) _latest[p.TagId] = p;
    }

    public IReadOnlyList<DataPoint> Snapshot() =>
        [.. _latest.Values.OrderBy(p => p.DeviceName, StringComparer.Ordinal).ThenBy(p => p.TagName, StringComparer.Ordinal)];

    public IReadOnlyList<DataPoint> Snapshot(long deviceId) =>
        [.. _latest.Values.Where(p => p.DeviceId == deviceId).OrderBy(p => p.TagName, StringComparer.Ordinal)];

    public DataPoint? Get(long tagId) => _latest.TryGetValue(tagId, out var p) ? p : null;

    /// <summary>设备配置变更/删除时清理其全部实时值。</summary>
    public void RemoveDevice(long deviceId)
    {
        foreach (var kv in _latest)
            if (kv.Value.DeviceId == deviceId)
                _latest.TryRemove(kv.Key, out _);
    }

    public void Clear() => _latest.Clear();

    public int Count => _latest.Count;
}
