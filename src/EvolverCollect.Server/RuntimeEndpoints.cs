namespace EvolverCollect.Server;

using System.Text.Json;
using EvolverCollect.Core.Abstractions;
using EvolverCollect.Core.Pipeline;
using EvolverCollect.Core.Runtime;
using EvolverCollect.Storage;

/// <summary>运行时 API：驱动元数据 / 连通测试 / 通道启停 / 实时值 / 写点位 / 北向状态。</summary>
public static class RuntimeEndpoints
{
    public static void MapRuntimeApi(this WebApplication app)
    {
        var repo = app.Services.GetRequiredService<ConfigRepository>();
        var channels = app.Services.GetRequiredService<ChannelManager>();
        var north = app.Services.GetRequiredService<NorthAppManager>();
        var cache = app.Services.GetRequiredService<RealtimeCache>();
        var driverFactories = app.Services.GetServices<IDriverFactory>().ToList();
        var northFactories = app.Services.GetServices<INorthboundFactory>().ToList();

        // 驱动/北向元数据（控制台据此渲染表单）
        app.MapGet("/api/drivers", () => driverFactories.Select(f => f.Descriptor)).RequireAuthorization();
        app.MapGet("/api/northbound-types", () => northFactories.Select(f => f.Descriptor)).RequireAuthorization();

        // 连通性测试：用给定连接参数建临时驱动实例探测
        app.MapPost("/api/channels/test", async (TestConnectionRequest req, CancellationToken ct) =>
        {
            var factory = driverFactories.FirstOrDefault(f => f.DriverId.Equals(req.DriverId, StringComparison.OrdinalIgnoreCase));
            if (factory is null) return Results.BadRequest(new { error = $"未知驱动: {req.DriverId}" });
            IDriver driver;
            try { driver = factory.Create(req.ConnectionJson ?? "{}"); }
            catch (Exception ex) { return Results.Ok(new { success = false, error = ex.Message }); }
            try
            {
                var result = await driver.ConnectAsync(ct);
                await driver.DisconnectAsync();
                return Results.Ok(new { success = result.Success, error = result.Error, elapsedMs = result.Elapsed.TotalMilliseconds });
            }
            catch (Exception ex) { return Results.Ok(new { success = false, error = ex.Message }); }
            finally { await driver.DisposeAsync(); }
        }).RequireAuthorization();

        // 通道启停（start = 已运行则热重启）
        app.MapPost("/api/channels/{id:long}/start", async (long id, CancellationToken ct) =>
        {
            if (channels.IsRunning(id)) await channels.RestartChannelAsync(id, ct);
            else await channels.StartChannelAsync(id, ct);
            return Results.Ok();
        }).RequireAuthorization();
        app.MapPost("/api/channels/{id:long}/stop", async (long id) =>
        {
            await channels.StopChannelAsync(id);
            return Results.Ok();
        }).RequireAuthorization();

        // 运行状态（配置与运行时状态合并）
        app.MapGet("/api/runtime/channels", async (CancellationToken ct) =>
        {
            var configs = await repo.GetChannelsAsync(ct);
            return configs.Select(c =>
            {
                var status = channels.GetStatus(c.Id);
                return new
                {
                    c.Id, c.Name, c.DriverId, c.Enabled,
                    State = status?.State.ToString() ?? "Stopped",
                    Connected = status?.Connected ?? false,
                    status?.SuccessRate,
                    status?.TotalReads,
                    status?.FailedReads,
                    status?.TotalPoints,
                    status?.LastError,
                    status?.LastErrorAt,
                    TagCount = status?.TagCount ?? 0,
                    DeviceCount = status?.DeviceCount ?? 0,
                    status?.StartedAt,
                    LatencyMs = status?.LastLatency.TotalMilliseconds,
                };
            });
        }).RequireAuthorization();

        // 实时值快照
        app.MapGet("/api/runtime/snapshot", (long? deviceId) =>
            Results.Json(deviceId is long id ? cache.Snapshot(id) : cache.Snapshot())).RequireAuthorization();
        app.MapGet("/api/runtime/stats", () => new
        {
            hubPublished = app.Services.GetRequiredService<DataHub>().PublishedPoints,
            hubDropped = app.Services.GetRequiredService<DataHub>().DroppedPoints,
            realtimeCount = cache.Count,
        }).RequireAuthorization();

        // 写点位
        app.MapPost("/api/runtime/write", async (WriteRequest req, CancellationToken ct) =>
        {
            var tags = await repo.GetTagsAsync(null, ct);
            var tag = tags.FirstOrDefault(t => t.Id == req.TagId)
                ?? throw new BadHttpRequestException($"点位 {req.TagId} 不存在");
            var devices = await repo.GetDevicesAsync(null, ct);
            var device = devices.FirstOrDefault(d => d.Id == tag.DeviceId)
                ?? throw new BadHttpRequestException($"点位所属设备不存在");

            var value = ConvertValue(req.Value, tag.DataType);
            var definition = ConfigRepository.ToTagDefinition(tag);
            var result = await channels.WriteTagAsync(device.ChannelId, definition, value, device.Name, device.StationNo, ct);
            return Results.Ok(result);
        }).RequireAuthorization();

        // 北向启停与状态
        app.MapGet("/api/runtime/north", () => north.GetStatusList()).RequireAuthorization();
        app.MapPost("/api/north-apps/{id:long}/start", async (long id, CancellationToken ct) =>
        {
            await north.StartAppAsync(id, ct);
            return Results.Ok();
        }).RequireAuthorization();
        app.MapPost("/api/north-apps/{id:long}/stop", async (long id) =>
        {
            await north.StopAppAsync(id);
            return Results.Ok();
        }).RequireAuthorization();
        app.MapPost("/api/north-apps/{id:long}/restart", async (long id, CancellationToken ct) =>
        {
            await north.RestartAppAsync(id, ct);
            return Results.Ok();
        }).RequireAuthorization();
    }

    private static object ConvertValue(JsonElement raw, string dataType)
    {
        var targetType = dataType switch
        {
            "Bool" => typeof(bool),
            "Int16" => typeof(short),
            "UInt16" => typeof(ushort),
            "Int32" => typeof(int),
            "UInt32" => typeof(uint),
            "Int64" => typeof(long),
            "UInt64" => typeof(ulong),
            "Float" => typeof(float),
            "Double" => typeof(double),
            _ => typeof(string),
        };
        var value = raw.ValueKind switch
        {
            JsonValueKind.String => raw.GetString()!,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => raw.GetRawText(),
        };
        return Convert.ChangeType(value, targetType);
    }

    public sealed record TestConnectionRequest(string DriverId, string? ConnectionJson);
    public sealed record WriteRequest(long TagId, JsonElement Value);
}
