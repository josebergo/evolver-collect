namespace EvolverCollect.Storage;

using Microsoft.EntityFrameworkCore;

/// <summary>建库 + 种子数据（默认管理员、默认设置项）。</summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IDbContextFactory<CollectDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();

        // v0.1 用 EnsureCreated（结构变更时删库重来的开发期行为）；上线前切换为 EF 迁移（ADR-0003）
        await db.Database.EnsureCreatedAsync();

        await SeedSettingsAsync(db);
        await SeedAdminAsync(db);
        await db.SaveChangesAsync();
    }

    private static async Task SeedSettingsAsync(CollectDbContext db)
    {
        var defaults = new Dictionary<string, string>
        {
            ["ListenHost"] = "127.0.0.1",
            ["ListenPort"] = "5080",
            ["SpoolMaxRows"] = "1000000",
            ["AlarmRetentionDays"] = "90",
            ["Language"] = "zh-CN",
            ["MustChangeDefaultPassword"] = "true",
        };
        foreach (var (key, value) in defaults)
            if (!db.Settings.Any(s => s.Key == key))
                db.Settings.Add(new SettingEntity { Key = key, Value = value });
    }

    private static async Task SeedAdminAsync(CollectDbContext db)
    {
        if (db.Users.Any(u => u.UserName == "admin")) return;
        db.Users.Add(new UserEntity
        {
            UserName = "admin",
            PasswordHash = PasswordHasher.Hash("admin"),
            Role = "admin",
            MustChangePassword = true, // 默认口令必须首登修改（工控安全基线）
        });
    }
}
