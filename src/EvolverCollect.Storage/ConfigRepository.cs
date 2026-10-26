namespace EvolverCollect.Storage;

using System.Text.Json;
using EvolverCollect.Core.Abstractions;
using EvolverCollect.Core.Domain;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// 配置仓储：全部配置 CRUD + 为运行时提供启动快照（IChannelConfigSource）。
/// 通过 IDbContextFactory 保证多线程安全；删除通道/设备时显式级联删除子项。
/// </summary>
public sealed class ConfigRepository(IDbContextFactory<CollectDbContext> factory) : IChannelConfigSource
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    // ============ 运行时快照（IChannelConfigSource） ============

    public async Task<IReadOnlyList<ChannelSnapshot>> LoadEnabledAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var channels = await db.Channels.AsNoTracking().Where(c => c.Enabled).ToListAsync(ct);
        var channelIds = channels.Select(c => c.Id).ToHashSet();
        var devices = await db.Devices.AsNoTracking().Where(d => d.Enabled && channelIds.Contains(d.ChannelId)).ToListAsync(ct);
        var deviceIds = devices.Select(d => d.Id).ToHashSet();
        var tags = await db.Tags.AsNoTracking().Where(t => deviceIds.Contains(t.DeviceId)).ToListAsync(ct);

        return [.. channels.Select(c => BuildSnapshot(c, devices, tags))];
    }

    public async Task<ChannelSnapshot?> LoadAsync(long channelId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var channel = await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId, ct);
        if (channel is null) return null;
        var devices = await db.Devices.AsNoTracking().Where(d => d.ChannelId == channelId && d.Enabled).ToListAsync(ct);
        var deviceIds = devices.Select(d => d.Id).ToHashSet();
        var tags = await db.Tags.AsNoTracking().Where(t => deviceIds.Contains(t.DeviceId)).ToListAsync(ct);
        return BuildSnapshot(channel, devices, tags);
    }

    private static ChannelSnapshot BuildSnapshot(
        ChannelEntity channel, List<DeviceEntity> devices, List<TagEntity> tags)
    {
        var deviceIds = devices.Select(d => d.Id).ToHashSet();
        return new ChannelSnapshot(
            new ChannelConfig(channel.Id, channel.Name, channel.DriverId, channel.Enabled,
                channel.ConnectionJson, channel.DefaultPollIntervalMs, channel.TimeoutMs,
                channel.RetryCount, channel.Description),
            [.. devices.Select(d => new DeviceConfig(d.Id, d.ChannelId, d.Name, d.StationNo, d.TemplateId, d.Enabled, d.Description))],
            [.. tags.Where(t => deviceIds.Contains(t.DeviceId)).Select(ToTagDefinition)]);
    }

    /// <summary>点位实体 → 运行时定义（驱动写值/测试复用）。</summary>
    public static TagDefinition ToTagDefinition(TagEntity t) => new(
        t.Id, t.DeviceId, t.Name, t.Address,
        Enum.TryParse<TagDataType>(t.DataType, out var dt) ? dt : TagDataType.Int16,
        t.Writable, t.PollIntervalMs, t.ScaleA, t.ScaleB, t.Deadband, t.BitIndex, t.Description);

    // ============ 北向 / 报警规则快照 ============

    public async Task<IReadOnlyList<NorthAppConfig>> LoadNorthAppsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var list = await db.NorthApps.AsNoTracking().OrderBy(n => n.Id).ToListAsync(ct);
        return [.. list.Select(ToNorthAppConfig)];
    }

    internal static NorthAppConfig ToNorthAppConfig(NorthAppEntity e) => new(
        e.Id, e.Name, e.Type, e.Enabled, e.ConnectionJson,
        new NorthAppOptions(e.IncludeFilter, e.ExcludeFilter, e.Format, e.BatchSize, e.BatchIntervalMs));

    public async Task<IReadOnlyList<AlarmRuleConfig>> LoadAlarmRulesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var list = await db.AlarmRules.AsNoTracking().Where(r => r.Enabled).OrderBy(r => r.Id).ToListAsync(ct);
        return [.. list.Select(r => new AlarmRuleConfig(r.Id, r.Name, r.TagId, r.Condition, r.Threshold,
            r.Level, r.DurationEvaluations, r.RecoverEvaluations, r.Enabled))];
    }

    // ============ 通道 ============

    public async Task<List<ChannelEntity>> GetChannelsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Channels.AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct);
    }

    public async Task<ChannelEntity?> GetChannelAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<ChannelEntity> CreateChannelAsync(ChannelEntity input, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        input.Id = 0;
        input.CreatedAt = DateTime.Now;
        db.Channels.Add(input);
        await db.SaveChangesAsync(ct);
        return input;
    }

    public async Task<bool> UpdateChannelAsync(long id, ChannelEntity input, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entity = await db.Channels.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entity is null) return false;
        entity.Name = input.Name;
        entity.DriverId = input.DriverId;
        entity.Enabled = input.Enabled;
        entity.ConnectionJson = input.ConnectionJson;
        entity.DefaultPollIntervalMs = input.DefaultPollIntervalMs;
        entity.TimeoutMs = input.TimeoutMs;
        entity.RetryCount = input.RetryCount;
        entity.Description = input.Description;
        entity.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task DeleteChannelAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var deviceIds = await db.Devices.Where(d => d.ChannelId == id).Select(d => d.Id).ToListAsync(ct);
        if (deviceIds.Count > 0)
            await db.Tags.Where(t => deviceIds.Contains(t.DeviceId)).ExecuteDeleteAsync(ct);
        await db.Devices.Where(d => d.ChannelId == id).ExecuteDeleteAsync(ct);
        await db.Channels.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
    }

    // ============ 设备 ============

    public async Task<List<DeviceEntity>> GetDevicesAsync(long? channelId = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.Devices.AsNoTracking().AsQueryable();
        if (channelId is not null) q = q.Where(d => d.ChannelId == channelId);
        return await q.OrderBy(d => d.ChannelId).ThenBy(d => d.Id).ToListAsync(ct);
    }

    public async Task<DeviceEntity> CreateDeviceAsync(DeviceEntity input, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        input.Id = 0;
        db.Devices.Add(input);
        await db.SaveChangesAsync(ct);
        return input;
    }

    public async Task<bool> UpdateDeviceAsync(long id, DeviceEntity input, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entity = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (entity is null) return false;
        entity.Name = input.Name;
        entity.ChannelId = input.ChannelId;
        entity.StationNo = input.StationNo;
        entity.TemplateId = input.TemplateId;
        entity.Enabled = input.Enabled;
        entity.Description = input.Description;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task DeleteDeviceAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Tags.Where(t => t.DeviceId == id).ExecuteDeleteAsync(ct);
        await db.Devices.Where(d => d.Id == id).ExecuteDeleteAsync(ct);
    }

    // ============ 点位 ============

    public async Task<List<TagEntity>> GetTagsAsync(long? deviceId = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.Tags.AsNoTracking().AsQueryable();
        if (deviceId is not null) q = q.Where(t => t.DeviceId == deviceId);
        return await q.OrderBy(t => t.DeviceId).ThenBy(t => t.SortOrder).ThenBy(t => t.Id).ToListAsync(ct);
    }

    public async Task<TagEntity> CreateTagAsync(TagEntity input, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        input.Id = 0;
        db.Tags.Add(input);
        await db.SaveChangesAsync(ct);
        return input;
    }

    public async Task<int> BatchCreateTagsAsync(IEnumerable<TagEntity> inputs, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var count = 0;
        foreach (var t in inputs)
        {
            t.Id = 0;
            db.Tags.Add(t);
            count++;
        }
        await db.SaveChangesAsync(ct);
        return count;
    }

    public async Task<bool> UpdateTagAsync(long id, TagEntity input, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entity = await db.Tags.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (entity is null) return false;
        entity.Name = input.Name;
        entity.Address = input.Address;
        entity.DataType = input.DataType;
        entity.Writable = input.Writable;
        entity.PollIntervalMs = input.PollIntervalMs;
        entity.ScaleA = input.ScaleA;
        entity.ScaleB = input.ScaleB;
        entity.Deadband = input.Deadband;
        entity.BitIndex = input.BitIndex;
        entity.Description = input.Description;
        entity.SortOrder = input.SortOrder;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task DeleteTagAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Tags.Where(t => t.Id == id).ExecuteDeleteAsync(ct);
    }

    // ============ 北向应用 ============

    public async Task<List<NorthAppEntity>> GetNorthAppsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.NorthApps.AsNoTracking().OrderBy(n => n.Id).ToListAsync(ct);
    }

    public async Task<NorthAppEntity> CreateNorthAppAsync(NorthAppEntity input, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        input.Id = 0;
        db.NorthApps.Add(input);
        await db.SaveChangesAsync(ct);
        return input;
    }

    public async Task<bool> UpdateNorthAppAsync(long id, NorthAppEntity input, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entity = await db.NorthApps.FirstOrDefaultAsync(n => n.Id == id, ct);
        if (entity is null) return false;
        entity.Name = input.Name;
        entity.Type = input.Type;
        entity.Enabled = input.Enabled;
        entity.ConnectionJson = input.ConnectionJson;
        entity.IncludeFilter = input.IncludeFilter;
        entity.ExcludeFilter = input.ExcludeFilter;
        entity.Format = input.Format;
        entity.BatchSize = input.BatchSize;
        entity.BatchIntervalMs = input.BatchIntervalMs;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task DeleteNorthAppAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Spool.Where(s => s.NorthAppId == id).ExecuteDeleteAsync(ct);
        await db.NorthApps.Where(n => n.Id == id).ExecuteDeleteAsync(ct);
    }

    // ============ 模板 ============

    public async Task<List<TemplateEntity>> GetTemplatesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Templates.AsNoTracking().OrderBy(t => t.Vendor).ThenBy(t => t.Model).ToListAsync(ct);
    }

    public async Task<TemplateEntity> CreateTemplateAsync(TemplateEntity input, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        input.Id = 0;
        db.Templates.Add(input);
        await db.SaveChangesAsync(ct);
        return input;
    }

    public async Task DeleteTemplateAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Templates.Where(t => t.Id == id).ExecuteDeleteAsync(ct);
        await db.Devices.Where(d => d.TemplateId == id).ExecuteUpdateAsync(s => s.SetProperty(d => d.TemplateId, (long?)null), ct);
    }

    /// <summary>把模板的点位应用到设备（同名校验去重），返回模板点位数。</summary>
    public async Task<int> ApplyTemplateAsync(long templateId, long deviceId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var template = await db.Templates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId, ct)
            ?? throw new InvalidOperationException($"模板 {templateId} 不存在");
        var tags = JsonSerializer.Deserialize<List<TemplateTag>>(template.TagsJson, WebJson) ?? [];
        var existing = await db.Tags.Where(t => t.DeviceId == deviceId).Select(t => t.Name).ToListAsync(ct);
        var existingSet = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var next = await db.Tags.Where(t => t.DeviceId == deviceId).MaxAsync(t => (int?)t.SortOrder, ct) ?? 0;
        foreach (var t in tags)
        {
            if (existingSet.Contains(t.Name)) continue;
            next++;
            db.Tags.Add(new TagEntity
            {
                DeviceId = deviceId,
                Name = t.Name,
                Address = t.Address,
                DataType = t.DataType,
                Writable = t.Writable,
                PollIntervalMs = t.PollIntervalMs,
                ScaleA = t.ScaleA,
                ScaleB = t.ScaleB,
                Deadband = t.Deadband,
                BitIndex = t.BitIndex,
                Description = t.Description,
                SortOrder = next,
            });
        }
        await db.SaveChangesAsync(ct);
        await db.Devices.Where(d => d.Id == deviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.TemplateId, templateId), ct);
        return tags.Count;
    }

    // ============ 报警 ============

    public async Task<List<AlarmRuleEntity>> GetAlarmRulesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AlarmRules.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct);
    }

    public async Task<AlarmRuleEntity> CreateAlarmRuleAsync(AlarmRuleEntity input, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        input.Id = 0;
        db.AlarmRules.Add(input);
        await db.SaveChangesAsync(ct);
        return input;
    }

    public async Task<bool> UpdateAlarmRuleAsync(long id, AlarmRuleEntity input, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entity = await db.AlarmRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (entity is null) return false;
        entity.Name = input.Name;
        entity.TagId = input.TagId;
        entity.Condition = input.Condition;
        entity.Threshold = input.Threshold;
        entity.Level = input.Level;
        entity.DurationEvaluations = input.DurationEvaluations;
        entity.RecoverEvaluations = input.RecoverEvaluations;
        entity.Enabled = input.Enabled;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task DeleteAlarmRuleAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.AlarmRules.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task<long> AddAlarmHistoryAsync(AlarmHistoryEntity input, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        input.Id = 0;
        db.AlarmHistory.Add(input);
        await db.SaveChangesAsync(ct);
        return input.Id;
    }

    public async Task<List<AlarmHistoryEntity>> QueryAlarmHistoryAsync(DateTime? since, DateTime? until, int limit = 200, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.AlarmHistory.AsNoTracking().AsQueryable();
        if (since is not null) q = q.Where(a => a.TriggeredAt >= since);
        if (until is not null) q = q.Where(a => a.TriggeredAt <= until);
        return await q.OrderByDescending(a => a.TriggeredAt).Take(limit).ToListAsync(ct);
    }

    public async Task MarkAlarmRecoveredAsync(long id, string recoverValue, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.AlarmHistory.Where(a => a.Id == id && a.RecoveredAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.RecoveredAt, DateTime.Now)
                .SetProperty(a => a.TriggerValue, a => a.TriggerValue + " → " + recoverValue), ct);
    }

    public async Task PurgeOldAlarmsAsync(int retentionDays, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var cutoff = DateTime.Now.AddDays(-retentionDays);
        await db.AlarmHistory.Where(a => a.TriggeredAt < cutoff).ExecuteDeleteAsync(ct);
    }

    // ============ 设置 / 用户 ============

    public async Task<Dictionary<string, string>> GetSettingsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entity = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (entity is null) db.Settings.Add(new SettingEntity { Key = key, Value = value });
        else entity.Value = value;
        await db.SaveChangesAsync(ct);
    }

    public async Task<UserEntity?> GetUserAsync(string userName, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserName == userName, ct);
    }

    public async Task<List<UserEntity>> GetUsersAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().OrderBy(u => u.Id).ToListAsync(ct);
    }

    public async Task<bool> ChangePasswordAsync(long userId, string newHash, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return false;
        user.PasswordHash = newHash;
        user.MustChangePassword = false;
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ============ 备份（整库配置导出/导入） ============

    public sealed record ExportBundle(
        List<ChannelEntity> Channels,
        List<DeviceEntity> Devices,
        List<TagEntity> Tags,
        List<NorthAppEntity> NorthApps,
        List<TemplateEntity> Templates,
        List<AlarmRuleEntity> AlarmRules,
        Dictionary<string, string> Settings);

    public async Task<ExportBundle> ExportAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return new ExportBundle(
            await db.Channels.AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct),
            await db.Devices.AsNoTracking().OrderBy(d => d.Id).ToListAsync(ct),
            await db.Tags.AsNoTracking().OrderBy(t => t.Id).ToListAsync(ct),
            await db.NorthApps.AsNoTracking().OrderBy(n => n.Id).ToListAsync(ct),
            await db.Templates.AsNoTracking().OrderBy(t => t.Id).ToListAsync(ct),
            await db.AlarmRules.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct),
            await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct));
    }

    /// <summary>整库导入（替换式）：清空配置表后按导出内容重建，保持原 Id。离线队列与报警历史不清除。</summary>
    public async Task ImportAllAsync(ExportBundle bundle, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Tags.ExecuteDeleteAsync(ct);
        await db.Devices.ExecuteDeleteAsync(ct);
        await db.Channels.ExecuteDeleteAsync(ct);
        await db.NorthApps.ExecuteDeleteAsync(ct);
        await db.Templates.ExecuteDeleteAsync(ct);
        await db.AlarmRules.ExecuteDeleteAsync(ct);

        db.Channels.AddRange(bundle.Channels);
        db.Devices.AddRange(bundle.Devices);
        db.Tags.AddRange(bundle.Tags);
        db.NorthApps.AddRange(bundle.NorthApps);
        db.Templates.AddRange(bundle.Templates);
        db.AlarmRules.AddRange(bundle.AlarmRules);
        foreach (var (key, value) in bundle.Settings)
        {
            var entity = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
            if (entity is null) db.Settings.Add(new SettingEntity { Key = key, Value = value });
            else entity.Value = value;
        }
        await db.SaveChangesAsync(ct);
    }
}
