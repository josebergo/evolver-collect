// FANUC FOCAS 适配器：stdio JSON 行协议。
// 背景（ADR-0004）：FOCAS 的 fwlib32.dll 是 32 位闭源库且授权禁止随包分发，
// 因此隔离为独立 x86 进程；主程序（AnyCPU/x64）通过本进程读写机床数据。
//
// 协议（每行一个 JSON 请求/响应）：
//   请求 {"cmd":"connect","host":"...","port":8193,"timeoutSec":5}
//   响应 {"ok":true,"library":"mock|fwlib32","error":null}
//   请求 {"cmd":"read"}
//   响应 {"ok":true,"data":{"status":"RUN","programNo":"O0001","spindleSpeed":1200,...}}
//   请求 {"cmd":"disconnect"}
//
// mock 模式：未找到 fwlib32.dll 时自动启用，生成连续变化的仿真机床数据，
// 无真机也能完整演示与联调主程序链路。

using System.Text.Json;
using System.Text.Json.Nodes;
using EvolverCollect.Adapter.Focas;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var useFwlib = args.Contains("--fwlib");
var library = FocasLibrary.Create(useFwlib);
Console.Error.WriteLine($"[focas-adapter] library = {library.Name}（mock=仿真数据）");

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
string? line;
while ((line = await Console.In.ReadLineAsync()) is not null)
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    JsonNode? request;
    try { request = JsonNode.Parse(line); }
    catch { await WriteAsync(new JsonObject { ["ok"] = false, ["error"] = "JSON 解析失败" }); continue; }

    var cmd = request?["cmd"]?.GetValue<string>() ?? "";
    var response = cmd switch
    {
        "connect" => HandleConnect(request!, library),
        "read" => HandleRead(library),
        "disconnect" => HandleDisconnect(library),
        _ => new JsonObject { ["ok"] = false, ["error"] = $"未知命令: {cmd}" },
    };
    await WriteAsync(response);
}

static async Task WriteAsync(JsonObject node)
{
    await Console.Out.WriteLineAsync(node.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
}

static JsonObject HandleConnect(JsonNode request, FocasLibrary library)
{
    var host = request["host"]?.GetValue<string>() ?? "192.168.1.100";
    var port = request["port"]?.GetValue<int>() ?? 8193;
    var result = library.Connect(host, port);
    return new JsonObject
    {
        ["ok"] = result.Success,
        ["library"] = library.Name,
        ["error"] = result.Error,
    };
}

static JsonObject HandleRead(FocasLibrary library)
{
    var data = library.ReadMachineData();
    return new JsonObject
    {
        ["ok"] = data is not null,
        ["error"] = data is null ? "未连接" : null,
        ["data"] = data is null
            ? null
            : JsonSerializer.SerializeToNode(data, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
    };
}

static JsonObject HandleDisconnect(FocasLibrary library)
{
    library.Disconnect();
    return new JsonObject { ["ok"] = true };
}
