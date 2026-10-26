namespace EvolverCollect.Northbound.Database;

using System.Globalization;
using System.Text;
using System.Text.Json;
using EvolverCollect.Core.Abstractions;
using MySqlConnector;

/// <summary>数据库北向出口：MySQL（批量 INSERT）与 InfluxDB v2（行协议 over HTTP）。</summary>
public sealed class DatabaseNorthboundSink(DatabaseConnectionOptions options) : INorthboundSink
{
    private MySqlConnection? _mysql;
    private readonly HttpClient _http = new();

    public string Type => "database";

    public async Task<ConnectResult> ConnectAsync(CancellationToken ct)
    {
        try
        {
            if (options.Provider == "mysql")
            {
                var builder = new MySqlConnectionStringBuilder
                {
                    Server = options.Host,
                    Port = (uint)options.Port,
                    UserID = options.UserName,
                    Password = options.Password ?? "",
                    Database = options.Database,
                    MaximumPoolSize = 5,
                };
                _mysql?.Dispose();
                _mysql = new MySqlConnection(builder.ConnectionString);
                await _mysql.OpenAsync(ct);
                await EnsureTableAsync(ct);
            }
            return ConnectResult.Ok();
        }
        catch (Exception ex)
        {
            return ConnectResult.Fail($"数据库连接失败: {ex.Message}");
        }
    }

    public async Task DisconnectAsync()
    {
        if (_mysql is not null) { await _mysql.CloseAsync(); _mysql.Dispose(); _mysql = null; }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _http.Dispose();
    }

    public async Task<bool> SendAsync(IReadOnlyList<DataPoint> points, string formattedPayload, CancellationToken ct) =>
        await SendRawPayloadAsync(points, ct);

    public async Task<bool> SendRawAsync(string payload, CancellationToken ct) => false; // 数据库无"原始报文"概念，回放走通用重试队列

    private async Task<bool> SendRawPayloadAsync(IReadOnlyList<DataPoint> points, CancellationToken ct)
    {
        try
        {
            if (options.Provider == "mysql")
                return await SendMySqlAsync(points, ct);
            return await SendInfluxAsync(points, ct);
        }
        catch { return false; }
    }

    private async Task<bool> SendMySqlAsync(IReadOnlyList<DataPoint> points, CancellationToken ct)
    {
        var connection = _mysql ?? throw new IOException("MySQL 未连接");
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);

        const string insertHead = "INSERT INTO collect_data (device_name, tag_name, value_num, value_str, quality, ts) VALUES ";
        var sb = new StringBuilder(insertHead.Length + points.Count * 80);
        sb.Append(insertHead);
        using var command = new MySqlCommand(sb.ToString(), connection);
        var i = 0;
        foreach (var p in points)
        {
            sb.Append(i == 0 ? "(" : ",(");
            sb.Append("@d").Append(i).Append(",@t").Append(i).Append(",@n").Append(i).Append(",@s").Append(i)
              .Append(",@q").Append(i).Append(",@ts").Append(i).Append(')');
            command.Parameters.AddWithValue($"@d{i}", p.DeviceName);
            command.Parameters.AddWithValue($"@t{i}", p.TagName);
            command.Parameters.AddWithValue($"@n{i}", p.Value is string ? DBNull.Value : Convert.ToDouble(p.Value, CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue($"@s{i}", p.Value?.ToString() ?? "");
            command.Parameters.AddWithValue($"@q{i}", p.Quality.ToString());
            command.Parameters.AddWithValue($"@ts{i}", DateTimeOffset.FromUnixTimeMilliseconds(p.TimestampMs).UtcDateTime);
            i++;
        }
        command.CommandText = sb.ToString();
        await command.ExecuteNonQueryAsync(ct);
        return true;
    }

    private async Task<bool> SendInfluxAsync(IReadOnlyList<DataPoint> points, CancellationToken ct)
    {
        // InfluxDB v2 行协议：measurement,device=X,tag=Y value=1.0,quality="Good" ts
        var sb = new StringBuilder(points.Count * 96);
        foreach (var p in points)
        {
            sb.Append(options.Database).Append(",device=").Append(Escape(p.DeviceName))
              .Append(",tag=").Append(Escape(p.TagName))
              .Append(" value=").Append(Convert.ToDouble(p.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture))
              .Append(",quality=\"").Append(p.Quality).Append("\" ")
              .Append(p.TimestampMs * 1000).Append('\n');
        }
        var url = $"http://{options.Host}:{options.Port}/api/v2/write?org={Uri.EscapeDataString(options.Org ?? "")}" +
                  $"&bucket={Uri.EscapeDataString(options.Database)}&precision=ms";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/plain"),
        };
        if (!string.IsNullOrEmpty(options.Password))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Token", options.Password);
        using var response = await _http.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    private async Task EnsureTableAsync(CancellationToken ct)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS collect_data (
                id BIGINT AUTO_INCREMENT PRIMARY KEY,
                device_name VARCHAR(100) NOT NULL,
                tag_name VARCHAR(100) NOT NULL,
                value_num DOUBLE NULL,
                value_str VARCHAR(500) NULL,
                quality VARCHAR(10) NOT NULL,
                ts DATETIME(3) NOT NULL,
                INDEX idx_ts (ts),
                INDEX idx_device_tag (device_name, tag_name)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            """;
        using var command = new MySqlCommand(ddl, _mysql!);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Escape(string s) => s.Replace(" ", @"\ ").Replace(",", @"\ ").Replace("=", @"\ ");
}

public sealed class DatabaseConnectionOptions
{
    /// <summary>mysql | influxdb。</summary>
    public string Provider { get; set; } = "mysql";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3306;
    public string Database { get; set; } = "evolver";
    /// <summary>MySQL=用户名；InfluxDB 可留空。</summary>
    public string? UserName { get; set; }
    /// <summary>MySQL=密码；InfluxDB=API Token。</summary>
    public string? Password { get; set; }
    /// <summary>InfluxDB org。</summary>
    public string? Org { get; set; }
}

public sealed class DatabaseNorthboundFactory : INorthboundFactory
{
    public static NorthboundDescriptor DescriptorValue { get; } = new(
        "database", "数据库（MySQL/InfluxDB）",
        """
        {
          "type": "object",
          "properties": {
            "provider": { "type": "string", "title": "类型", "enum": ["mysql", "influxdb"], "default": "mysql" },
            "host": { "type": "string", "title": "主机", "default": "127.0.0.1" },
            "port": { "type": "integer", "title": "端口", "default": 3306,
                      "description": "MySQL 3306 / InfluxDB 8086" },
            "database": { "type": "string", "title": "库/桶", "default": "evolver" },
            "userName": { "type": "string", "title": "用户名", "description": "InfluxDB 留空" },
            "password": { "type": "string", "title": "密码/Token", "format": "password" },
            "org": { "type": "string", "title": "InfluxDB Org", "description": "仅 InfluxDB" }
          },
          "required": ["provider", "host", "database"]
        }
        """,
        "northbound/database");

    public string Type => "database";
    public NorthboundDescriptor Descriptor => DescriptorValue;

    public INorthboundSink Create(string connectionJson, NorthAppOptions options)
    {
        var conn = JsonSerializer.Deserialize<DatabaseConnectionOptions>(
            string.IsNullOrWhiteSpace(connectionJson) ? "{}" : connectionJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new DatabaseConnectionOptions();
        return new DatabaseNorthboundSink(conn);
    }
}
