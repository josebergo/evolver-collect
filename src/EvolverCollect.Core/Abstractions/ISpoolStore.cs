namespace EvolverCollect.Core.Abstractions;

/// <summary>
/// 离线数据队列（断点续传）存储接口。
/// 数据按批次入队（自增 Id 保序），北向恢复后按序回放，发送成功后推进水位。
/// </summary>
public interface ISpoolStore
{
    Task EnqueueAsync(long northAppId, string payload, CancellationToken ct = default);

    /// <summary>按 Id 升序取出一批待回放数据（不删除，回放成功后再 MarkSent）。</summary>
    Task<IReadOnlyList<SpoolItem>> DequeueAsync(long northAppId, int max, CancellationToken ct = default);

    /// <summary>删除 Id &lt;= upToId 的已发送数据。</summary>
    Task MarkSentAsync(long northAppId, long upToId, CancellationToken ct = default);

    /// <summary>FIFO 淘汰：仅保留最近 maxRows 行。</summary>
    Task TrimAsync(long northAppId, long maxRows, CancellationToken ct = default);

    Task<long> GetDepthAsync(long northAppId, CancellationToken ct = default);
    Task<long> GetTotalDepthAsync(CancellationToken ct = default);
}

public sealed record SpoolItem(long Id, long NorthAppId, string Payload, DateTime CreatedAtUtc);
