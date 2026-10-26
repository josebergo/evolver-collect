namespace EvolverCollect.Northbound.Kafka;

using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using EvolverCollect.Core.Abstractions;

/// <summary>Kafka 北向出口：批次报文写入 Topic，按设备名作为消息 Key（同设备保序进同一分区）。</summary>
public sealed class KafkaNorthboundSink(KafkaConnectionOptions options) : INorthboundSink
{
    private IProducer<string, string>? _producer;
    private bool _connected;

    public string Type => "kafka";

    public Task<ConnectResult> ConnectAsync(CancellationToken ct)
    {
        try
        {
            var config = new ProducerConfig
            {
                BootstrapServers = options.BootstrapServers,
                MessageTimeoutMs = Math.Max(1000, options.TimeoutSeconds * 1000),
                MessageSendMaxRetries = 2,
            };
            if (!string.IsNullOrEmpty(options.SaslUsername))
            {
                config.SecurityProtocol = options.Ssl ? SecurityProtocol.SaslSsl : SecurityProtocol.SaslPlaintext;
                config.SaslMechanism = SaslMechanism.Plain;
                config.SaslUsername = options.SaslUsername;
                config.SaslPassword = options.SaslPassword ?? "";
            }
            else if (options.Ssl)
            {
                config.SecurityProtocol = SecurityProtocol.Ssl;
            }
            _producer?.Dispose();
            _producer = new ProducerBuilder<string, string>(config).Build();
            _connected = true; // 生产者惰性建连；真正的失败由 Send 返回 false → 离线队列兜底
            return Task.FromResult(ConnectResult.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(ConnectResult.Fail($"Kafka 初始化失败: {ex.Message}"));
        }
    }

    public Task DisconnectAsync()
    {
        _connected = false;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_producer is not null)
        {
            try { _producer.Flush(TimeSpan.FromSeconds(3)); } catch { }
            _producer.Dispose();
            _producer = null;
        }
        await ValueTask.CompletedTask;
    }

    public async Task<bool> SendAsync(IReadOnlyList<DataPoint> points, string formattedPayload, CancellationToken ct)
    {
        if (!_connected || _producer is null) return false;
        try
        {
            var lastDevice = points.Count > 0 ? points[^1].DeviceName : "default";
            await _producer.ProduceAsync(options.Topic, new Message<string, string>
            {
                Key = lastDevice,
                Value = formattedPayload,
            }, ct);
            return true;
        }
        catch { return false; }
    }

    public Task<bool> SendRawAsync(string payload, CancellationToken ct)
    {
        if (!_connected || _producer is null) return Task.FromResult(false);
        try
        {
            _producer.Produce(options.Topic, new Message<string, string> { Key = "spool", Value = payload });
            return Task.FromResult(true);
        }
        catch { return Task.FromResult(false); }
    }
}

public sealed class KafkaConnectionOptions
{
    public string BootstrapServers { get; set; } = "127.0.0.1:9092";
    public string Topic { get; set; } = "evolver-collect";
    public string? SaslUsername { get; set; }
    public string? SaslPassword { get; set; }
    public bool Ssl { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
}

public sealed class KafkaNorthboundFactory : INorthboundFactory
{
    public static NorthboundDescriptor DescriptorValue { get; } = new(
        "kafka", "Kafka",
        """
        {
          "type": "object",
          "properties": {
            "bootstrapServers": { "type": "string", "title": "Bootstrap Servers", "default": "127.0.0.1:9092" },
            "topic": { "type": "string", "title": "Topic", "default": "evolver-collect" },
            "saslUsername": { "type": "string", "title": "SASL 用户名" },
            "saslPassword": { "type": "string", "title": "SASL 密码", "format": "password" },
            "ssl": { "type": "boolean", "title": "SSL", "default": false },
            "timeoutSeconds": { "type": "integer", "title": "投递超时(秒)", "default": 10 }
          },
          "required": ["bootstrapServers", "topic"]
        }
        """,
        "northbound/kafka");

    public string Type => "kafka";
    public NorthboundDescriptor Descriptor => DescriptorValue;

    public INorthboundSink Create(string connectionJson, NorthAppOptions options)
    {
        var conn = JsonSerializer.Deserialize<KafkaConnectionOptions>(
            string.IsNullOrWhiteSpace(connectionJson) ? "{}" : connectionJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new KafkaConnectionOptions();
        return new KafkaNorthboundSink(conn);
    }
}
