namespace EvolverCollect.Storage;

using EvolverCollect.Core.Abstractions;
using Microsoft.EntityFrameworkCore;

/// <summary>SQLite 离线队列（断点续传）。Id 自增即全局顺序，按（NorthAppId, Id）索引回放。</summary>
public sealed class SqliteSpoolStore(IDbContextFactory<CollectDbContext> factory) : ISpoolStore
{
    public async Task EnqueueAsync(long northAppId, string payload, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Spool.Add(new SpoolEntity { NorthAppId = northAppId, Payload = payload, CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SpoolItem>> DequeueAsync(long northAppId, int max, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Spool.AsNoTracking()
            .Where(s => s.NorthAppId == northAppId)
            .OrderBy(s => s.Id)
            .Take(max)
            .ToListAsync(ct);
        return [.. rows.Select(r => new SpoolItem(r.Id, r.NorthAppId, r.Payload, r.CreatedAtUtc))];
    }

    public async Task MarkSentAsync(long northAppId, long upToId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Spool.Where(s => s.NorthAppId == northAppId && s.Id <= upToId).ExecuteDeleteAsync(ct);
    }

    public async Task TrimAsync(long northAppId, long maxRows, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // FIFO 淘汰：保留最近 maxRows 行，删除更早的
        var threshold = await db.Spool.AsNoTracking()
            .Where(s => s.NorthAppId == northAppId)
            .OrderByDescending(s => s.Id)
            .Skip((int)Math.Min(maxRows, int.MaxValue))
            .Select(s => (long?)s.Id)
            .FirstOrDefaultAsync(ct);
        if (threshold is not null)
            await db.Spool.Where(s => s.NorthAppId == northAppId && s.Id <= threshold.Value).ExecuteDeleteAsync(ct);
    }

    public async Task<long> GetDepthAsync(long northAppId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Spool.LongCountAsync(s => s.NorthAppId == northAppId, ct);
    }

    public async Task<long> GetTotalDepthAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Spool.LongCountAsync(ct);
    }
}
