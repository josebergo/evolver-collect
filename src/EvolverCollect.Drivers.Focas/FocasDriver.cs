namespace EvolverCollect.Drivers.Focas;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EvolverCollect.Core.Abstractions;

/// <summary>
/// FANUC FOCAS 驱动：拉起独立 x86 适配器进程（evolver-focas-adapter.exe），
/// 经 stdio JSON 行协议读取机床数据（ADR-0004：隔离闭源 32 位 fwlib32.dll）。
/// 适配器无 fwlib32 时进入 mock 模式，可完整演示。
/// 地址 = 机床数据字段名：status / programNo / spindleSpeed / spindleLoad / feedRate / partsCount / alarmNo / absolutePosition。
/// </summary>
public sealed class FocasDriver(FocasOptions options) : IDriver
{
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    public DriverDescriptor Descriptor => FocasDriverFactory.DescriptorValue;
    public DriverStats Stats { get; } = new();

    public async Task<ConnectResult> ConnectAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var exe = ResolveAdapterPath();
            if (exe is null)
                return ConnectResult.Fail("未找到 evolver-focas-adapter.exe（FOCAS 适配器）", sw.Elapsed);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = options.UseFwlib ? "--fwlib" : "",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            _process = Process.Start(psi)
                ?? throw new InvalidOperationException("适配器进程启动失败");
            _stdin = _process.StandardInput;
            _stdin.AutoFlush = true;
            _stdout = _process.StandardOutput;
            _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.Error.WriteLine(e.Data); };
            _process.BeginErrorReadLine();

            var response = await SendAsync(new JsonObject
            {
                ["cmd"] = "connect",
                ["host"] = options.Host,
                ["port"] = options.Port,
            }, ct);
            if (response?["ok"]?.GetValue<bool>() == true)
                return ConnectResult.Ok(sw.Elapsed);
            return ConnectResult.Fail($"FOCAS 连接失败: {response?["error"]}", sw.Elapsed);
        }
        catch (Exception ex)
        {
            return ConnectResult.Fail($"适配器启动失败: {ex.Message}", sw.Elapsed);
        }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            if (_stdin is not null)
                await _stdin.WriteLineAsync("""{"cmd":"disconnect"}""");
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await _process.WaitForExitAsync(killCts.Token); } catch (OperationCanceledException) { }
            }
        }
        catch { }
        _process?.Dispose();
        _process = null;
        _stdin = null;
        _stdout = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _ioLock.Dispose();
    }

    public ITagBrowser? CreateTagBrowser() => null;

    public async Task<IReadOnlyList<DataPoint>> ReadAsync(DeviceBatch batch, CancellationToken ct)
    {
        var ts = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        JsonObject? response;
        try
        {
            await _ioLock.WaitAsync(ct);
            try { response = await SendAsync(new JsonObject { ["cmd"] = "read" }, ct); }
            finally { _ioLock.Release(); }
        }
        catch (Exception ex)
        {
            Stats.RecordRead(default, false, batch.Tags.Count, ex.Message);
            throw new IOException($"FOCAS 读取失败: {ex.Message}", ex);
        }

        var data = response?["data"]?.AsObject();
        var points = new List<DataPoint>(batch.Tags.Count);
        foreach (var tag in batch.Tags)
        {
            var node = data?[tag.Address];
            if (node is null)
            {
                points.Add(DataPoint.BadPoint(batch.ChannelId, batch.DeviceId, tag.Id, batch.DeviceName, tag.Name,
                    $"未知字段 {tag.Address}（支持 status/programNo/spindleSpeed/spindleLoad/feedRate/partsCount/alarmNo/absolutePosition）", ts));
                continue;
            }
            object? value = tag.DataType switch
            {
                TagDataType.Bool => node.GetValue<string>() is "RUN" or "1" or "true",
                TagDataType.String => node.GetValue<string>(),
                TagDataType.Float or TagDataType.Double => node.GetValue<double>(),
                _ => node.GetValue<int>(),
            };
            points.Add(new DataPoint(batch.ChannelId, batch.DeviceId, tag.Id, batch.DeviceName, tag.Name, value, Quality.Good, ts));
        }
        Stats.RecordRead(default, true, batch.Tags.Count);
        return points;
    }

    public Task<WriteResult> WriteAsync(TagDefinition tag, object value, string deviceName, int? stationNo, CancellationToken ct) =>
        Task.FromResult(WriteResult.Fail("FOCAS 数据项为只读"));

    private async Task<JsonObject?> SendAsync(JsonObject request, CancellationToken ct)
    {
        var stdout = _stdout ?? throw new IOException("适配器未启动");
        await _stdin!.WriteLineAsync(request.ToJsonString(JsonOptions));
        var line = await stdout.ReadLineAsync(ct);
        return line is null ? null : JsonNode.Parse(line)?.AsObject();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private string? ResolveAdapterPath()
    {
        if (!string.IsNullOrWhiteSpace(options.AdapterPath) && File.Exists(options.AdapterPath))
            return options.AdapterPath;
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "evolver-focas-adapter.exe"),
            Path.Combine(baseDir, "adapters", "evolver-focas-adapter.exe"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "src",
                "EvolverCollect.Adapter.Focas", "bin", "Debug", "net10.0", "evolver-focas-adapter.exe")),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}

public sealed class FocasOptions
{
    public string Host { get; set; } = "192.168.1.100";
    public int Port { get; set; } = 8193;
    /// <summary>适配器完整路径（留空自动查找）。</summary>
    public string? AdapterPath { get; set; }
    /// <summary>要求使用真机 fwlib32（默认允许 mock）。</summary>
    public bool UseFwlib { get; set; }
}

public sealed class FocasDriverFactory : IDriverFactory
{
    public const string DriverIdValue = "focas";

    public static DriverDescriptor DescriptorValue { get; } = new(
        DriverIdValue, "FANUC FOCAS", "FANUC",
        """
        {
          "type": "object",
          "properties": {
            "host": { "type": "string", "title": "机床 IP", "default": "192.168.1.100" },
            "port": { "type": "integer", "title": "FOCAS 端口", "default": 8193 },
            "adapterPath": { "type": "string", "title": "适配器路径",
              "description": "留空自动查找 evolver-focas-adapter.exe" },
            "useFwlib": { "type": "boolean", "title": "强制真机模式", "default": false,
              "description": "默认：无 fwlib32.dll 时用 mock 仿真数据（演示/联调）" }
          }
        }
        """,
        DriverCapabilities.Read,
        "机床数据字段：status(Bool=RUN) / programNo(String) / spindleSpeed(Int32) / spindleLoad(Float) / feedRate(Float) / partsCount(Int32) / alarmNo(Int32) / absolutePosition(String)",
        "developer/focas-adapter");

    public string DriverId => DriverIdValue;
    public DriverDescriptor Descriptor => DescriptorValue;

    public IDriver Create(string connectionJson)
    {
        var options = JsonSerializer.Deserialize<FocasOptions>(
            string.IsNullOrWhiteSpace(connectionJson) ? "{}" : connectionJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new FocasOptions();
        return new FocasDriver(options);
    }
}
