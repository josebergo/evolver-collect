using System.Security.Claims;
using EvolverCollect.Core.Abstractions;
using EvolverCollect.Core.Domain;
using EvolverCollect.Core.Pipeline;
using EvolverCollect.Core.Runtime;
using EvolverCollect.Drivers.Modbus;
using EvolverCollect.Drivers.Mc;
using EvolverCollect.Drivers.Fins;
using EvolverCollect.Drivers.S7;
using EvolverCollect.Drivers.OpcUa;
using EvolverCollect.Drivers.Focas;
using EvolverCollect.Northbound.Http;
using EvolverCollect.Northbound.Kafka;
using EvolverCollect.Northbound.Database;
using EvolverCollect.Northbound.Mqtt;
using EvolverCollect.Core.Alarm;
using Microsoft.AspNetCore.SignalR;
using EvolverCollect.Server;
using EvolverCollect.Server.Auth;
using EvolverCollect.Server.Components;
using EvolverCollect.Server.Realtime;
using EvolverCollect.Server.Services;
using EvolverCollect.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ---------- 存储与核心服务 ----------
var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);

builder.Services.AddDbContextFactory<CollectDbContext>(options =>
    options.UseSqlite($"Data Source={Path.Combine(dataDir, "evolver.db")}"));

builder.Services.AddSingleton<DataHub>();
builder.Services.AddSingleton<RealtimeCache>();
builder.Services.AddSingleton<ConfigRepository>();
builder.Services.AddSingleton<IChannelConfigSource>(sp => sp.GetRequiredService<ConfigRepository>());
builder.Services.AddSingleton<SqliteSpoolStore>();
builder.Services.AddSingleton<ISpoolStore>(sp => sp.GetRequiredService<SqliteSpoolStore>());

// ---------- 南向驱动 / 北向出口（新增驱动在此注册即可被控制台自动发现） ----------
builder.Services.AddSingleton<IDriverFactory, ModbusDriverFactory>();
builder.Services.AddSingleton<IDriverFactory, OpcUaDriverFactory>();
builder.Services.AddSingleton<IDriverFactory, S7DriverFactory>();
builder.Services.AddSingleton<IDriverFactory, McDriverFactory>();
builder.Services.AddSingleton<IDriverFactory, FinsDriverFactory>();
builder.Services.AddSingleton<IDriverFactory, FocasDriverFactory>();
builder.Services.AddSingleton<INorthboundFactory, MqttNorthboundFactory>();
builder.Services.AddSingleton<INorthboundFactory, HttpNorthboundFactory>();
builder.Services.AddSingleton<INorthboundFactory, KafkaNorthboundFactory>();
builder.Services.AddSingleton<INorthboundFactory, DatabaseNorthboundFactory>();
builder.Services.AddSingleton<AlarmService>();

builder.Services.AddSingleton<ChannelManager>();
builder.Services.AddSingleton<NorthAppManager>();
builder.Services.AddSingleton<ConnectivityTester>();
builder.Services.AddSingleton<RealtimeService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<DocsService>();

// ---------- Web ----------
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.AccessDeniedPath = "/login";
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddSignalR();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddOpenApi();
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

var app = builder.Build();

// ---------- 初始化 ----------
await DatabaseInitializer.InitializeAsync(
    app.Services.GetRequiredService<IDbContextFactory<CollectDbContext>>());

// 模板库种子：把 templates/ 下的 JSON 首次导入（同名不重复导入）
await SeedTemplatesAsync(app.Services.GetRequiredService<ConfigRepository>());

static async Task SeedTemplatesAsync(ConfigRepository repo)
{
    // 发布态：templates 跟随 exe；开发态：向上找到仓库根的 templates/
    var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "templates") };
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    for (var i = 0; i < 6 && dir.Parent is not null; i++)
    {
        dir = dir.Parent;
        candidates.Add(Path.Combine(dir.FullName, "templates"));
    }
    var templatesDir = candidates.FirstOrDefault(Directory.Exists);
    if (templatesDir is null) return;

    var existing = (await repo.GetTemplatesAsync())
        .Select(t => $"{t.Vendor}|{t.Model}").ToHashSet(StringComparer.OrdinalIgnoreCase);

    foreach (var file in Directory.EnumerateFiles(templatesDir, "*.json", SearchOption.AllDirectories))
    {
        try
        {
            var json = await File.ReadAllTextAsync(file);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var vendor = doc.RootElement.GetProperty("vendor").GetString() ?? "";
            var model = doc.RootElement.GetProperty("model").GetString() ?? "";
            if (string.IsNullOrEmpty(vendor) || existing.Contains($"{vendor}|{model}")) continue;
            await repo.CreateTemplateAsync(new TemplateEntity
            {
                Vendor = vendor,
                Model = model,
                Description = doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() : null,
                TagsJson = doc.RootElement.GetProperty("tags").GetRawText(),
            });
        }
        catch { /* 模板文件损坏跳过 */ }
    }
}

// 监听地址来自设置（默认仅本机）
var settings = app.Services.GetRequiredService<ConfigRepository>().GetSettingsAsync().GetAwaiter().GetResult();
var host = settings.GetValueOrDefault("ListenHost", "127.0.0.1");
var port = settings.GetValueOrDefault("ListenPort", "5080");
app.Urls.Add($"http://{host}:{port}");

// 启动全部启用的通道与北向（含实时服务预热）
_ = app.Services.GetRequiredService<RealtimeService>();
await app.Services.GetRequiredService<ChannelManager>().StartAllAsync();
await app.Services.GetRequiredService<NorthAppManager>().StartAllAsync();

// 报警引擎：加载规则 + 历史落库 + SignalR 推送
var alarmService = app.Services.GetRequiredService<AlarmService>();
var repoForAlarm = app.Services.GetRequiredService<ConfigRepository>();
alarmService.HistorySink = alarm => repoForAlarm
    .AddAlarmHistoryAsync(new AlarmHistoryEntity
    {
        RuleId = alarm.RuleId, RuleName = alarm.RuleName, TagId = alarm.TagId,
        TagName = alarm.TagName, DeviceName = alarm.DeviceName, Level = alarm.Level,
        TriggerValue = alarm.TriggerValue, Message = alarm.Message,
    }).GetAwaiter().GetResult();
alarmService.AlarmRaised += alarm =>
{
    _ = app.Services.GetRequiredService<IHubContext<MonitorHub>>()
        .Clients.All.SendAsync(MonitorHub.Alarms, alarm);
};
alarmService.Start(await repoForAlarm.LoadAlarmRulesAsync());

app.Lifetime.ApplicationStopping.Register(() =>
{
    try
    {
        app.Services.GetRequiredService<NorthAppManager>().StopAllAsync().GetAwaiter().GetResult();
        app.Services.GetRequiredService<ChannelManager>().StopAllAsync().GetAwaiter().GetResult();
    }
    catch { /* 停机清理尽力而为 */ }
});

// ---------- 中间件 ----------
if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// 登出（GET，本地工具可接受）
app.MapGet("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    ctx.Response.Redirect("/login");
});

// 浏览器表单登录（本地工具；API 集成方可用 /api/auth/login）
app.MapPost("/login-submit", async (HttpContext ctx, AuthService auth) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var result = await auth.LoginAsync(
        form["userName"].ToString(), form["password"].ToString());

    if (!result.Success || result.Principal is null)
    {
        ctx.Response.Redirect("/login?error=1");
        return;
    }
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        result.Principal, AuthService.CookieProperties());
    ctx.Response.Redirect(result.MustChangePassword ? "/settings" : "/");
});

app.MapPost("/api/auth/login", async (LoginRequest req, HttpContext ctx, AuthService auth) =>
{
    var result = await auth.LoginAsync(req.UserName, req.Password);
    if (!result.Success || result.Principal is null)
        return Results.BadRequest(new { error = result.Error });
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        result.Principal, AuthService.CookieProperties());
    return Results.Ok(new { mustChangePassword = result.MustChangePassword });
});

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    product = "evolver-collect",
    version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
})).RequireAuthorization();

app.MapConfigApi();
app.MapRuntimeApi();

// ---------- 文档中心 API ----------
var docs = app.Services.GetRequiredService<DocsService>();
app.MapGet("/api/docs", () => docs.GetToc());
app.MapGet("/api/docs/search", (string? q) => docs.Search(q ?? ""));
app.MapGet("/api/docs/content", (string slug) =>
    docs.Get(slug) is { } entry ? Results.Ok(entry) : Results.NotFound());

// 控制台
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapHub<MonitorHub>("/hubs/monitor");

Console.WriteLine();
Console.WriteLine($"  Evolver Collect 已启动: http://{host}:{port}   （默认账号 admin/admin，首登请改密）");
Console.WriteLine();

app.Run();

/// <summary>API 登录请求体。</summary>
public sealed record LoginRequest(string UserName, string Password);
