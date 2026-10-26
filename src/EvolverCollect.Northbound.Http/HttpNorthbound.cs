namespace EvolverCollect.Northbound.Http;

using System.Text;
using System.Text.Json;
using EvolverCollect.Core.Abstractions;

/// <summary>HTTP 北向出口：把批次报文 POST 到指定 URL（对接自研平台/MES 的最简方式）。</summary>
public sealed class HttpNorthboundSink(HttpConnectionOptions options) : INorthboundSink
{
    private readonly HttpClient _client = new()
    {
        Timeout = TimeSpan.FromSeconds(Math.Max(3, options.TimeoutSeconds)),
    };

    public string Type => "http";

    public Task<ConnectResult> ConnectAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Url))
            return Task.FromResult(ConnectResult.Fail("未配置目标 Url"));
        return Task.FromResult(ConnectResult.Ok());
    }

    public Task DisconnectAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    public async Task<bool> SendAsync(IReadOnlyList<DataPoint> points, string formattedPayload, CancellationToken ct)
    {
        try
        {
            using var request = BuildRequest(formattedPayload);
            using var response = await _client.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> SendRawAsync(string payload, CancellationToken ct)
    {
        try
        {
            using var request = BuildRequest(payload);
            using var response = await _client.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private HttpRequestMessage BuildRequest(string payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, options.Url)
        {
            Content = new StringContent(payload, Encoding.UTF8, options.ContentType),
        };
        if (!string.IsNullOrEmpty(options.HeaderName) && !string.IsNullOrEmpty(options.HeaderValue))
            request.Headers.TryAddWithoutValidation(options.HeaderName, options.HeaderValue);
        return request;
    }
}

public sealed class HttpConnectionOptions
{
    public string Url { get; set; } = "";
    /// <summary>自定义鉴权头（如 Authorization: Bearer xxx）。</summary>
    public string? HeaderName { get; set; }
    public string? HeaderValue { get; set; }
    public string ContentType { get; set; } = "application/json";
    public int TimeoutSeconds { get; set; } = 10;
}

public sealed class HttpNorthboundFactory : INorthboundFactory
{
    public static NorthboundDescriptor DescriptorValue { get; } = new(
        "http", "HTTP 推送",
        """
        {
          "type": "object",
          "properties": {
            "url": { "type": "string", "title": "目标地址", "default": "http://127.0.0.1:9000/ingest",
                     "description": "批次报文将以 POST + 配置的 Content-Type 发送到该地址" },
            "headerName": { "type": "string", "title": "自定义头名称", "description": "如 Authorization" },
            "headerValue": { "type": "string", "title": "自定义头值", "format": "password", "description": "如 Bearer xxx" },
            "contentType": { "type": "string", "title": "Content-Type", "default": "application/json",
                             "enum": ["application/json", "text/plain", "application/x-ndjson"] },
            "timeoutSeconds": { "type": "integer", "title": "超时(秒)", "default": 10 }
          },
          "required": ["url"]
        }
        """,
        "northbound/http");

    public string Type => "http";
    public NorthboundDescriptor Descriptor => DescriptorValue;

    public INorthboundSink Create(string connectionJson, NorthAppOptions options)
    {
        var conn = JsonSerializer.Deserialize<HttpConnectionOptions>(
            string.IsNullOrWhiteSpace(connectionJson) ? "{}" : connectionJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new HttpConnectionOptions();
        return new HttpNorthboundSink(conn);
    }
}
