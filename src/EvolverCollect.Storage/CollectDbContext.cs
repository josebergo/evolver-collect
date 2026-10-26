namespace EvolverCollect.Storage;

using Microsoft.EntityFrameworkCore;

/// <summary>SQLite 单库承载：配置 + 报警历史 + 离线队列（v0.1 用 EnsureCreated，迁移框架见 ADR-0003）。</summary>
public class CollectDbContext(DbContextOptions<CollectDbContext> options) : DbContext(options)
{
    public DbSet<ChannelEntity> Channels => Set<ChannelEntity>();
    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();
    public DbSet<TagEntity> Tags => Set<TagEntity>();
    public DbSet<NorthAppEntity> NorthApps => Set<NorthAppEntity>();
    public DbSet<AlarmRuleEntity> AlarmRules => Set<AlarmRuleEntity>();
    public DbSet<AlarmHistoryEntity> AlarmHistory => Set<AlarmHistoryEntity>();
    public DbSet<TemplateEntity> Templates => Set<TemplateEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<SettingEntity> Settings => Set<SettingEntity>();
    public DbSet<SpoolEntity> Spool => Set<SpoolEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeviceEntity>()
            .HasIndex(d => d.ChannelId);

        modelBuilder.Entity<TagEntity>()
            .HasIndex(t => t.DeviceId);

        modelBuilder.Entity<TagEntity>()
            .HasIndex(t => new { t.DeviceId, t.Name }).IsUnique();

        // 离线队列按（北向应用, 自增 Id）回放，复合索引保证按序取出与水位删除高效
        modelBuilder.Entity<SpoolEntity>()
            .HasIndex(s => new { s.NorthAppId, s.Id });

        modelBuilder.Entity<AlarmHistoryEntity>()
            .HasIndex(a => a.TriggeredAt);

        base.OnModelCreating(modelBuilder);
    }
}
