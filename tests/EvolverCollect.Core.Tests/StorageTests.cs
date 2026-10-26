namespace EvolverCollect.Core.Tests;

using EvolverCollect.Core.Abstractions;
using EvolverCollect.Core.Domain;
using EvolverCollect.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

public class StorageTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IDbContextFactory<CollectDbContext> _factory;

    public StorageTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"evolver-test-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<CollectDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _factory = new SimpleDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    private sealed class SimpleDbContextFactory(DbContextOptions<CollectDbContext> options)
        : IDbContextFactory<CollectDbContext>
    {
        public CollectDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task Channel_device_tag_snapshot_maps_correctly()
    {
        var repo = new ConfigRepository(_factory);
        var channel = await repo.CreateChannelAsync(new ChannelEntity
        {
            Name = "电表通道", DriverId = "modbus-tcp", ConnectionJson = """{"host":"10.0.0.5"}""",
            DefaultPollIntervalMs = 500,
        });
        var device = await repo.CreateDeviceAsync(new DeviceEntity
        {
            ChannelId = channel.Id, Name = "电表-01", StationNo = 3, Enabled = true,
        });
        await repo.BatchCreateTagsAsync(
        [
            new TagEntity { DeviceId = device.Id, Name = "电压", Address = "40001", DataType = "Float" },
            new TagEntity { DeviceId = device.Id, Name = "电流", Address = "40003", DataType = "Float" },
            new TagEntity { DeviceId = device.Id, Name = "运行", Address = "00001", DataType = "Bool" },
        ]);

        var snapshots = await repo.LoadEnabledAsync();
        var snapshot = Assert.Single(snapshots);
        Assert.Equal(channel.Id, snapshot.Channel.Id);
        Assert.Equal("modbus-tcp", snapshot.Channel.DriverId);
        Assert.Equal("电表-01", Assert.Single(snapshot.Devices).Name);
        Assert.Equal(3, Assert.Single(snapshot.Devices).StationNo);
        Assert.Equal(3, snapshot.Tags.Count);
        Assert.Equal(TagDataType.Float, snapshot.Tags[0].DataType);
        Assert.Equal("电压", snapshot.Tags[0].Name);
    }

    [Fact]
    public async Task Disabled_device_tags_excluded_from_snapshot()
    {
        var repo = new ConfigRepository(_factory);
        var channel = await repo.CreateChannelAsync(new ChannelEntity { Name = "c", DriverId = "modbus-tcp" });
        var d1 = await repo.CreateDeviceAsync(new DeviceEntity { ChannelId = channel.Id, Name = "启用", Enabled = true });
        var d2 = await repo.CreateDeviceAsync(new DeviceEntity { ChannelId = channel.Id, Name = "停用", Enabled = false });
        await repo.BatchCreateTagsAsync(
        [
            new TagEntity { DeviceId = d1.Id, Name = "a", Address = "40001" },
            new TagEntity { DeviceId = d2.Id, Name = "b", Address = "40002" },
        ]);

        var snapshot = Assert.Single(await repo.LoadEnabledAsync());
        Assert.Equal("a", Assert.Single(snapshot.Tags).Name);
    }

    [Fact]
    public async Task Delete_channel_cascades_devices_and_tags()
    {
        var repo = new ConfigRepository(_factory);
        var channel = await repo.CreateChannelAsync(new ChannelEntity { Name = "c", DriverId = "modbus-tcp" });
        var device = await repo.CreateDeviceAsync(new DeviceEntity { ChannelId = channel.Id, Name = "d" });
        await repo.BatchCreateTagsAsync([new TagEntity { DeviceId = device.Id, Name = "t", Address = "40001" }]);

        await repo.DeleteChannelAsync(channel.Id);

        Assert.Empty(await repo.GetChannelsAsync());
        Assert.Empty(await repo.GetDevicesAsync());
        Assert.Empty(await repo.GetTagsAsync());
    }

    [Fact]
    public async Task Spool_store_preserves_order_and_watermark()
    {
        var spool = new SqliteSpoolStore(_factory);
        for (var i = 1; i <= 5; i++)
            await spool.EnqueueAsync(9, $"payload-{i}");

        Assert.Equal(5, await spool.GetDepthAsync(9));

        var batch = await spool.DequeueAsync(9, 3);
        Assert.Equal(new[] { "payload-1", "payload-2", "payload-3" }, batch.Select(b => b.Payload));

        await spool.MarkSentAsync(9, batch[^1].Id);
        Assert.Equal(2, await spool.GetDepthAsync(9));

        var rest = await spool.DequeueAsync(9, 10);
        Assert.Equal(new[] { "payload-4", "payload-5" }, rest.Select(b => b.Payload));

        await spool.TrimAsync(9, 1);
        Assert.Equal(1, await spool.GetDepthAsync(9));
        Assert.Equal("payload-5", (await spool.DequeueAsync(9, 10)).Single().Payload);
    }

    [Fact]
    public void Password_hash_roundtrip()
    {
        var hash = PasswordHasher.Hash("s3cret!");
        Assert.NotEqual("s3cret!", hash);
        Assert.True(PasswordHasher.Verify("s3cret!", hash));
        Assert.False(PasswordHasher.Verify("wrong", hash));
        Assert.False(PasswordHasher.Verify("s3cret!", "garbage"));
    }

    [Fact]
    public async Task Template_apply_skips_existing_names()
    {
        var repo = new ConfigRepository(_factory);
        var channel = await repo.CreateChannelAsync(new ChannelEntity { Name = "c", DriverId = "modbus-tcp" });
        var device = await repo.CreateDeviceAsync(new DeviceEntity { ChannelId = channel.Id, Name = "d" });
        await repo.BatchCreateTagsAsync([new TagEntity { DeviceId = device.Id, Name = "已存在", Address = "HR0" }]);

        var template = await repo.CreateTemplateAsync(new TemplateEntity
        {
            Vendor = "演示", Model = "DEMO-100",
            TagsJson = """[{"name":"已存在","address":"HR0","dataType":"UInt16"},{"name":"新增","address":"HR1","dataType":"Float"}]""",
        });

        var applied = await repo.ApplyTemplateAsync(template.Id, device.Id);
        var tags = await repo.GetTagsAsync(device.Id);
        Assert.Equal(2, tags.Count); // 同名跳过，只新增 1 个
        Assert.Equal(2, applied);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }
}
