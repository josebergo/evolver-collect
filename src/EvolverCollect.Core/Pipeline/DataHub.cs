namespace EvolverCollect.Core.Pipeline;

using System.Threading.Channels;
using EvolverCollect.Core.Abstractions;

/// <summary>
/// 数据枢纽：采集运行时的唯一发布入口，向所有订阅者（北向/报警/实时缓存）扇出。
/// 设计要点：
/// - 有界通道 + Wait 模式 → 满时 Publish 返回 false（丢弃整批并计数），削峰不阻塞采集；
/// - 订阅回调要求快速非阻塞（订阅方自行入队异步消费），单个订阅者异常互不影响；
/// - 单读者分发循环保证订阅者看到的批次顺序与发布顺序一致。
/// </summary>
public sealed class DataHub : IAsyncDisposable
{
    private readonly Channel<IReadOnlyList<DataPoint>> _channel;
    private readonly List<Action<IReadOnlyList<DataPoint>>> _subscribers = [];
    private readonly object _gate = new();
    private readonly Task _dispatcher;
    private long _publishedPoints;
    private long _droppedPoints;

    public DataHub(int capacity = 100_000)
    {
        _channel = Channel.CreateBounded<IReadOnlyList<DataPoint>>(
            new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _dispatcher = Task.Run(ProcessAsync);
    }

    /// <summary>发布一批数据点。返回 false 表示管道已满被丢弃（调用方可降低频率或告警）。</summary>
    public bool Publish(IReadOnlyList<DataPoint> points)
    {
        if (points.Count == 0) return true;
        if (!_channel.Writer.TryWrite(points))
        {
            Interlocked.Add(ref _droppedPoints, points.Count);
            return false;
        }
        Interlocked.Add(ref _publishedPoints, points.Count);
        return true;
    }

    /// <summary>订阅数据流。回调必须快速返回（自行异步消费），异常会被隔离。</summary>
    public IDisposable Subscribe(Action<IReadOnlyList<DataPoint>> handler)
    {
        lock (_gate) _subscribers.Add(handler);
        return new Subscription(() => { lock (_gate) _subscribers.Remove(handler); });
    }

    public long PublishedPoints => Interlocked.Read(ref _publishedPoints);
    public long DroppedPoints => Interlocked.Read(ref _droppedPoints);

    private async Task ProcessAsync()
    {
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var batch))
                {
                    Action<IReadOnlyList<DataPoint>>[] handlers;
                    lock (_gate) handlers = [.. _subscribers];
                    foreach (var h in handlers)
                    {
                        try { h(batch); }
                        catch { /* 订阅者异常隔离：单个订阅者不能拖垮分发 */ }
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* 正常关闭 */ }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try { await _dispatcher.ConfigureAwait(false); } catch { /* 忽略分发循环收尾异常 */ }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
