namespace EvolverCollect.Northbound.Mqtt;

using System.Text;
using System.Text.Json;
using EvolverCollect.Core.Abstractions;
using EvolverCollect.Core.Pipeline;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

/// <summary>
/// MQTT 北向出口：批次报文发布到主题模板。主题支持 {device} 占位（按设备分主题发布，如每台机床一个 topic）。
/// </summary>
public sealed class MqttNorthboundSink(MqttConnectionOptions options, NorthAppOptions appOptions) : INorthboundSink
{
    private IMqttClient? _client;

    public string Type => "mqtt";

    public async Task<ConnectResult> ConnectAsync(CancellationToken ct)
    {
        try
        {
            if (_client is not null)
            {
                try { await _client.DisconnectAsync().ConfigureAwait(false); } catch { /* 旧连接清理 */ }
                _client.Dispose();
            }
            var client = new MqttFactory().CreateMqttClient();
            var builder = new MqttClientOptionsBuilder()
                .WithTcpServer(options.Host, options.Port)
                .WithClientId(options.ClientId)
                .WithProtocolVersion(MqttProtocolVersion.V500)
                .WithCleanSession();
            if (!string.IsNullOrEmpty(options.Username))
                builder.WithCredentials(options.Username, options.Password);
            if (options.Tls)
                builder.WithTlsOptions(o => o.WithCertificateValidationHandler(_ => options.AllowUntrustedCertificate));

            await client.ConnectAsync(builder.Build(), ct).ConfigureAwait(false);
            _client = client;
            return ConnectResult.Ok();
        }
        catch (Exception ex)
        {
            return ConnectResult.Fail($"MQTT 连接失败: {ex.Message}");
        }
    }

    public async Task DisconnectAsync()
    {
        if (_client is not null)
        {
            try { await _client.DisconnectAsync().ConfigureAwait(false); } catch { }
            _client.Dispose();
            _client = null;
        }
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        await ValueTask.CompletedTask;
    }

    public async Task<bool> SendAsync(IReadOnlyList<DataPoint> points, string formattedPayload, CancellationToken ct)
    {
        var client = _client;
        if (client is null || !client.IsConnected) return false;

        try
        {
            if (options.TopicTemplate.Contains("{device}", StringComparison.Ordinal))
            {
                // 按设备分主题：同批不同设备的数据拆开、各自重新格式化
                foreach (var group in points.GroupBy(p => p.DeviceName))
                {
                    var payload = NorthFormat.FormatBatch([.. group], appOptions.Format);
                    await PublishAsync(client, options.TopicTemplate.Replace("{device}", group.Key), payload, ct).ConfigureAwait(false);
                }
            }
            else
            {
                await PublishAsync(client, options.TopicTemplate, formattedPayload, ct).ConfigureAwait(false);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task<bool> SendRawAsync(string payload, CancellationToken ct)
    {
        // 回放积压：{device} 主题无法还原分组，统一发布到 spool 子主题
        var topic = options.TopicTemplate.Replace("{device}", "spool");
        var client = _client;
        if (client is null || !client.IsConnected) return Task.FromResult(false);
        return PublishAsync(client, topic, payload, ct);
    }

    private async Task<bool> PublishAsync(IMqttClient client, string topic, string payload, CancellationToken ct)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(payload))
            .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)Math.Clamp(options.Qos, 0, 1))
            .WithRetainFlag(options.Retain)
            .Build();
        await client.PublishAsync(message, ct).ConfigureAwait(false);
        return true;
    }
}

/// <summary>MQTT 连接参数。</summary>
public sealed class MqttConnectionOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 1883;
    public string ClientId { get; set; } = $"evolver-collect-{Guid.NewGuid():N}"[..32];
    public string? Username { get; set; }
    public string? Password { get; set; }
    /// <summary>主题模板，支持 {device} 占位。</summary>
    public string TopicTemplate { get; set; } = "evolver-collect/data";
    /// <summary>0=至多一次 1=至少一次。</summary>
    public int Qos { get; set; }
    public bool Retain { get; set; }
    public bool Tls { get; set; }
    public bool AllowUntrustedCertificate { get; set; }
}

/// <summary>MQTT 北向工厂。</summary>
public sealed class MqttNorthboundFactory : INorthboundFactory
{
    public static NorthboundDescriptor DescriptorValue { get; } = new(
        Type: "mqtt",
        DisplayName: "MQTT",
        ParameterSchemaJson: """
        {
          "type": "object",
          "properties": {
            "host": { "type": "string", "title": "Broker 地址", "default": "127.0.0.1" },
            "port": { "type": "integer", "title": "端口", "default": 1883 },
            "clientId": { "type": "string", "title": "Client ID" },
            "username": { "type": "string", "title": "用户名" },
            "password": { "type": "string", "title": "密码", "format": "password" },
            "topicTemplate": { "type": "string", "title": "主题模板", "default": "evolver-collect/data",
                               "description": "支持 {device} 占位实现按设备分主题" },
            "qos": { "type": "integer", "title": "QoS", "enum": [0, 1], "default": 0 },
            "retain": { "type": "boolean", "title": "保留消息", "default": false },
            "tls": { "type": "boolean", "title": "TLS", "default": false },
            "allowUntrustedCertificate": { "type": "boolean", "title": "允许自签名证书", "default": false }
          },
          "required": ["host", "port", "topicTemplate"]
        }
        """,
        DocsSlug: "northbound/mqtt");

    public string Type => "mqtt";
    public NorthboundDescriptor Descriptor => DescriptorValue;

    public INorthboundSink Create(string connectionJson, NorthAppOptions options)
    {
        var conn = JsonSerializer.Deserialize<MqttConnectionOptions>(
            string.IsNullOrWhiteSpace(connectionJson) ? "{}" : connectionJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new MqttConnectionOptions();
        return new MqttNorthboundSink(conn, options);
    }
}
