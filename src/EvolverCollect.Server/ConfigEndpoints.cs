namespace EvolverCollect.Server;

using EvolverCollect.Storage;
using Microsoft.EntityFrameworkCore;
using MiniExcelLibs;

/// <summary>配置管理 REST API（通道/设备/点位/北向/模板/报警规则/设置/备份）。</summary>
public static class ConfigEndpoints
{
    public static void MapConfigApi(this WebApplication app)
    {
        var repo = app.Services.GetRequiredService<ConfigRepository>();

        // ---------- 通道 ----------
        var channels = app.MapGroup("/api/channels").RequireAuthorization();
        channels.MapGet("/", async (CancellationToken ct) => await repo.GetChannelsAsync(ct));
        channels.MapGet("/{id:long}", async (long id, CancellationToken ct) =>
            await repo.GetChannelAsync(id, ct) is { } c ? Results.Ok(c) : Results.NotFound());
        channels.MapPost("/", async (ChannelEntity input, CancellationToken ct) =>
        {
            var created = await repo.CreateChannelAsync(input, ct);
            return Results.Created($"/api/channels/{created.Id}", created);
        });
        channels.MapPut("/{id:long}", async (long id, ChannelEntity input, CancellationToken ct) =>
            await repo.UpdateChannelAsync(id, input, ct) ? Results.Ok() : Results.NotFound());
        channels.MapDelete("/{id:long}", async (long id, CancellationToken ct) =>
        {
            await repo.DeleteChannelAsync(id, ct);
            return Results.Ok();
        });

        // ---------- 设备 ----------
        var devices = app.MapGroup("/api/devices").RequireAuthorization();
        devices.MapGet("/", async (long? channelId, CancellationToken ct) => await repo.GetDevicesAsync(channelId, ct));
        devices.MapPost("/", async (DeviceEntity input, CancellationToken ct) =>
        {
            var created = await repo.CreateDeviceAsync(input, ct);
            return Results.Created($"/api/devices/{created.Id}", created);
        });
        devices.MapPut("/{id:long}", async (long id, DeviceEntity input, CancellationToken ct) =>
            await repo.UpdateDeviceAsync(id, input, ct) ? Results.Ok() : Results.NotFound());
        devices.MapDelete("/{id:long}", async (long id, CancellationToken ct) =>
        {
            await repo.DeleteDeviceAsync(id, ct);
            return Results.Ok();
        });

        // ---------- 点位 ----------
        var tags = app.MapGroup("/api/tags").RequireAuthorization();
        tags.MapGet("/", async (long? deviceId, CancellationToken ct) => await repo.GetTagsAsync(deviceId, ct));
        tags.MapPost("/", async (TagEntity input, CancellationToken ct) =>
        {
            var created = await repo.CreateTagAsync(input, ct);
            return Results.Created($"/api/tags/{created.Id}", created);
        });
        tags.MapPost("/batch", async (List<TagEntity> inputs, CancellationToken ct) =>
            Results.Ok(new { created = await repo.BatchCreateTagsAsync(inputs, ct) }));
        tags.MapPut("/{id:long}", async (long id, TagEntity input, CancellationToken ct) =>
            await repo.UpdateTagAsync(id, input, ct) ? Results.Ok() : Results.NotFound());
        tags.MapDelete("/{id:long}", async (long id, CancellationToken ct) =>
        {
            await repo.DeleteTagAsync(id, ct);
            return Results.Ok();
        });

        // ---------- 北向应用 ----------
        var north = app.MapGroup("/api/north-apps").RequireAuthorization();
        north.MapGet("/", async (CancellationToken ct) => await repo.GetNorthAppsAsync(ct));
        north.MapPost("/", async (NorthAppEntity input, CancellationToken ct) =>
        {
            var created = await repo.CreateNorthAppAsync(input, ct);
            return Results.Created($"/api/north-apps/{created.Id}", created);
        });
        north.MapPut("/{id:long}", async (long id, NorthAppEntity input, CancellationToken ct) =>
            await repo.UpdateNorthAppAsync(id, input, ct) ? Results.Ok() : Results.NotFound());
        north.MapDelete("/{id:long}", async (long id, CancellationToken ct) =>
        {
            await repo.DeleteNorthAppAsync(id, ct);
            return Results.Ok();
        });

        // ---------- 模板 ----------
        var templates = app.MapGroup("/api/templates").RequireAuthorization();
        templates.MapGet("/", async (CancellationToken ct) => await repo.GetTemplatesAsync(ct));
        templates.MapPost("/", async (TemplateEntity input, CancellationToken ct) =>
        {
            var created = await repo.CreateTemplateAsync(input, ct);
            return Results.Created($"/api/templates/{created.Id}", created);
        });
        templates.MapDelete("/{id:long}", async (long id, CancellationToken ct) =>
        {
            await repo.DeleteTemplateAsync(id, ct);
            return Results.Ok();
        });
        templates.MapPost("/{id:long}/apply", async (long id, ApplyTemplateRequest req, CancellationToken ct) =>
        {
            var count = await repo.ApplyTemplateAsync(id, req.DeviceId, ct);
            return Results.Ok(new { applied = count });
        });

        // ---------- 报警规则 ----------
        var rules = app.MapGroup("/api/alarm-rules").RequireAuthorization();
        rules.MapGet("/", async (CancellationToken ct) => await repo.GetAlarmRulesAsync(ct));
        rules.MapPost("/", async (AlarmRuleEntity input, CancellationToken ct) =>
        {
            var created = await repo.CreateAlarmRuleAsync(input, ct);
            return Results.Created($"/api/alarm-rules/{created.Id}", created);
        });
        rules.MapPut("/{id:long}", async (long id, AlarmRuleEntity input, CancellationToken ct) =>
            await repo.UpdateAlarmRuleAsync(id, input, ct) ? Results.Ok() : Results.NotFound());
        rules.MapDelete("/{id:long}", async (long id, CancellationToken ct) =>
        {
            await repo.DeleteAlarmRuleAsync(id, ct);
            return Results.Ok();
        });
        app.MapGet("/api/alarms", async (DateTime? since, DateTime? until, int? limit, CancellationToken ct) =>
            await repo.QueryAlarmHistoryAsync(since, until, limit ?? 200, ct)).RequireAuthorization();

        // ---------- 设置 / 备份 ----------
        app.MapGet("/api/settings", async (CancellationToken ct) => await repo.GetSettingsAsync(ct)).RequireAuthorization();
        app.MapPut("/api/settings", async (SettingPair req, CancellationToken ct) =>
        {
            await repo.SetSettingAsync(req.Key, req.Value, ct);
            return Results.Ok();
        }).RequireAuthorization();
        app.MapGet("/api/system/export", async (CancellationToken ct) => await repo.ExportAllAsync(ct)).RequireAuthorization();
        app.MapPost("/api/system/import", async (ConfigRepository.ExportBundle bundle, CancellationToken ct) =>
        {
            await repo.ImportAllAsync(bundle, ct);
            return Results.Ok(new { imported = true });
        }).RequireAuthorization();

        // ---------- Excel 点表导入导出（MiniExcel，xlsx/csv） ----------
        app.MapGet("/api/tags/export-excel", async (long? deviceId, CancellationToken ct) =>
        {
            var tags = await repo.GetTagsAsync(deviceId, ct);
            var rows = tags.Select(t => new
            {
                名称 = t.Name, 地址 = t.Address, 类型 = t.DataType, 可写 = t.Writable ? "是" : "",
                周期ms = t.PollIntervalMs, 系数a = t.ScaleA, 偏移b = t.ScaleB,
                死区 = t.Deadband, 位序号 = t.BitIndex, 说明 = t.Description,
            });
            var stream = new MemoryStream();
            await MiniExcel.SaveAsAsync(stream, rows, cancellationToken: ct);
            stream.Position = 0;
            return Results.File(stream, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"tags-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx");
        }).RequireAuthorization();

        app.MapPost("/api/tags/import-excel", async (long deviceId, IFormFile file, CancellationToken ct) =>
        {
            using var upload = file.OpenReadStream();
            var rows = (await MiniExcel.QueryAsync(upload, cancellationToken: ct)).Cast<IDictionary<string, object?>>().ToList();
            var entities = new List<TagEntity>();
            var sortOrder = 0;
            foreach (var row in rows)
            {
                var name = Cell(row, "名称");
                var address = Cell(row, "地址");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(address)) continue;
                entities.Add(new TagEntity
                {
                    DeviceId = deviceId,
                    Name = name,
                    Address = address,
                    DataType = string.IsNullOrEmpty(Cell(row, "类型")) ? "UInt16" : Cell(row, "类型")!,
                    Writable = Cell(row, "可写") == "是",
                    PollIntervalMs = int.TryParse(Cell(row, "周期ms"), out var p) ? p : null,
                    ScaleA = double.TryParse(Cell(row, "系数a"), out var a) ? a : 1.0,
                    ScaleB = double.TryParse(Cell(row, "偏移b"), out var b) ? b : 0.0,
                    Deadband = double.TryParse(Cell(row, "死区"), out var d) ? d : null,
                    BitIndex = int.TryParse(Cell(row, "位序号"), out var bit) ? bit : null,
                    Description = Cell(row, "说明"),
                    SortOrder = sortOrder++,
                });
            }
            var created = await repo.BatchCreateTagsAsync(entities, ct);
            return Results.Ok(new { created });
        }).DisableAntiforgery().RequireAuthorization();
    }

    public sealed record ApplyTemplateRequest(long DeviceId);
    public sealed record SettingPair(string Key, string Value);

    private static string? Cell(IDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) ? value?.ToString()?.Trim() : null;
}
