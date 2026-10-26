namespace EvolverCollect.Drivers.OpcUa;

using System.Diagnostics;
using System.Text.Json;
using EvolverCollect.Core.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

/// <summary>
/// OPC UA 驱动（官方 .NET 栈）。地址语法 = NodeId 文本（ns=2;s=Channel1.Tag1 或 i=2258）。
/// 支持地址空间浏览导入；轮询读取（订阅模式在 v1.x 计划中）。
/// </summary>
public sealed class OpcUaDriver(OpcUaOptions options) : IDriver
{
    private Session? _session;

    public DriverDescriptor Descriptor => OpcUaDriverFactory.DescriptorValue;
    public DriverStats Stats { get; } = new();

    public async Task<ConnectResult> ConnectAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var config = new ApplicationConfiguration
            {
                ApplicationName = "EvolverCollect",
                ApplicationUri = Utils.Format("urn:{0}:EvolverCollect", System.Net.Dns.GetHostName()),
                ApplicationType = ApplicationType.Client,
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier(),
                    AutoAcceptUntrustedCertificates = true,
                    RejectSHA1SignedCertificates = false,
                    MinimumCertificateKeySize = 1024,
                },
                TransportConfigurations = new TransportConfigurationCollection(),
                TransportQuotas = new TransportQuotas { OperationTimeout = Math.Max(5000, options.TimeoutMs * 3) },
                ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60_000 },
            };
            await config.Validate(ApplicationType.Client);
            if (options.AutoAcceptUntrustedCertificates)
                config.CertificateValidator.CertificateValidation += (_, e) =>
                {
                    if (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted) e.Accept = true;
                };

            var endpointDescription = CoreClientUtils.SelectEndpoint(
                config, options.EndpointUrl, options.UseSecurity, 15000);
            var endpoint = new ConfiguredEndpoint(null, endpointDescription, EndpointConfiguration.Create(config));

            var identity = options.Anonymous || string.IsNullOrEmpty(options.UserName)
                ? new UserIdentity()
                : new UserIdentity(options.UserName, System.Text.Encoding.UTF8.GetBytes(options.Password ?? ""));

            _session = await Session.Create(config, endpoint,
                updateBeforeConnect: false, checkDomain: false,
                sessionName: "EvolverCollect", sessionTimeout: 60_000,
                identity: identity, preferredLocales: null);
            return ConnectResult.Ok(sw.Elapsed);
        }
        catch (Exception ex)
        {
            return ConnectResult.Fail($"OPC UA 连接失败: {ex.Message}", sw.Elapsed);
        }
    }

    public Task DisconnectAsync()
    {
        _session?.Close();
        _session?.Dispose();
        _session = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _session?.Dispose();
        _session = null;
        await ValueTask.CompletedTask;
    }

    public ITagBrowser? CreateTagBrowser() => _session is null ? null : new OpcUaTagBrowser(_session);

    public async Task<IReadOnlyList<DataPoint>> ReadAsync(DeviceBatch batch, CancellationToken ct)
    {
        var session = _session ?? throw new IOException("OPC UA 会话未建立");
        var sw = Stopwatch.StartNew();
        var ts = DateTimeOffset.Now.ToUnixTimeMilliseconds();

        var nodes = new ReadValueIdCollection(batch.Tags.Count);
        foreach (var tag in batch.Tags)
        {
            NodeId nodeId;
            try { nodeId = NodeId.Parse(tag.Address); }
            catch (Exception ex)
            {
                return BadAll(batch, $"NodeId 不合法: {ex.Message}", ts);
            }
            nodes.Add(new ReadValueId { NodeId = nodeId, AttributeId = Attributes.Value });
        }

        try
        {
            var response = await session.ReadAsync(null, 0, TimestampsToReturn.Both, nodes, ct);
            sw.Stop();
            Stats.RecordRead(sw.Elapsed, true, batch.Tags.Count);

            var points = new List<DataPoint>(batch.Tags.Count);
            for (var i = 0; i < batch.Tags.Count; i++)
            {
                var dv = response.Results[i];
                var tag = batch.Tags[i];
                var quality = StatusCodeToQuality(dv.StatusCode);
                var value = quality == Quality.Bad ? StatusCode.LookupSymbolicId(dv.StatusCode.Code) : dv.Value;
                points.Add(new DataPoint(batch.ChannelId, batch.DeviceId, tag.Id,
                    batch.DeviceName, tag.Name, value, quality, ts));
            }
            return points;
        }
        catch (ServiceResultException ex)
        {
            sw.Stop();
            Stats.RecordRead(sw.Elapsed, false, batch.Tags.Count, ex.Message);
            throw new IOException($"OPC UA 读取失败: {ex.Message}", ex); // 链路级 → 运行时重连
        }
    }

    public async Task<WriteResult> WriteAsync(TagDefinition tag, object value, string deviceName, int? stationNo, CancellationToken ct)
    {
        var session = _session ?? throw new IOException("OPC UA 会话未建立");
        if (!tag.Writable) return WriteResult.Fail("点位未开启可写");
        try
        {
            var nodeId = NodeId.Parse(tag.Address);
            var writeValue = new WriteValue
            {
                NodeId = nodeId,
                AttributeId = Attributes.Value,
                Value = new DataValue(new Variant(tag.DataType == TagDataType.String
                    ? Convert.ToString(value)
                    : Convert.ToDouble(value))),
            };
            var response = await session.WriteAsync(null, new WriteValueCollection { writeValue }, ct);
            var result = response.Results[0];
            return StatusCode.IsGood(result)
                ? WriteResult.Ok()
                : WriteResult.Fail(StatusCode.LookupSymbolicId(result.Code));
        }
        catch (Exception ex)
        {
            return WriteResult.Fail(ex.Message);
        }
    }

    internal static Quality StatusCodeToQuality(StatusCode sc) => sc.Code switch
    {
        StatusCodes.Good => Quality.Good,
        _ when (sc.Code & 0x8000_0000) != 0 => Quality.Bad,
        _ when (sc.Code & 0x4000_0000) != 0 => Quality.Uncertain,
        _ => Quality.Good,
    };

    private IReadOnlyList<DataPoint> BadAll(DeviceBatch batch, string reason, long ts) =>
        [.. batch.Tags.Select(t => DataPoint.BadPoint(batch.ChannelId, batch.DeviceId, t.Id, batch.DeviceName, t.Name, reason, ts))];
}

/// <summary>OPC UA 连接参数。</summary>
public sealed class OpcUaOptions
{
    /// <summary>如 opc.tcp://127.0.0.1:4840</summary>
    public string EndpointUrl { get; set; } = "opc.tcp://127.0.0.1:4840";
    public bool UseSecurity { get; set; }
    public bool AutoAcceptUntrustedCertificates { get; set; } = true;
    public bool Anonymous { get; set; } = true;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public int TimeoutMs { get; set; } = 5000;
}

/// <summary>OPC UA 地址空间浏览器（懒加载树）。</summary>
internal sealed class OpcUaTagBrowser(Session session) : ITagBrowser
{
    public Task<IReadOnlyList<TagNode>> BrowseAsync(string? parentId, CancellationToken ct = default)
    {
        var browser = new Browser(session)
        {
            BrowseDirection = BrowseDirection.Forward,
            IncludeSubtypes = true,
            ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
            ContinueUntilDone = false,
            MaxReferencesReturned = 500,
        };
        var refs = string.IsNullOrEmpty(parentId)
            ? browser.Browse(Objects.RootFolder)
            : browser.Browse(parentId);

        var nodes = refs.Select(r => new TagNode(
            Id: r.NodeId.ToString(),
            Name: r.DisplayName.Text,
            DataType: r.NodeClass == NodeClass.Variable ? "Variable" : null,
            Writable: r.NodeClass == NodeClass.Variable,
            HasChildren: r.NodeClass is NodeClass.Object or NodeClass.ObjectType or NodeClass.View
                         or NodeClass.Variable && r.BrowseName != null,
            Address: r.NodeId.ToString())).ToList();
        return Task.FromResult<IReadOnlyList<TagNode>>(nodes);
    }
}

public sealed class OpcUaDriverFactory : IDriverFactory
{
    public const string DriverIdValue = "opcua";

    public static DriverDescriptor DescriptorValue { get; } = new(
        DriverIdValue, "OPC UA", "OPC 基金会标准",
        """
        {
          "type": "object",
          "properties": {
            "endpointUrl": { "type": "string", "title": "端点地址", "default": "opc.tcp://127.0.0.1:4840" },
            "useSecurity": { "type": "boolean", "title": "加密(SecurityPolicy)", "default": false },
            "autoAcceptUntrustedCertificates": { "type": "boolean", "title": "接受未受信证书", "default": true,
              "description": "现场自签名证书常见；公网环境请关闭" },
            "anonymous": { "type": "boolean", "title": "匿名登录", "default": true },
            "userName": { "type": "string", "title": "用户名(非匿名)" },
            "password": { "type": "string", "title": "密码", "format": "password" },
            "timeoutMs": { "type": "integer", "title": "超时(ms)", "default": 5000 }
          },
          "required": ["endpointUrl"]
        }
        """,
        DriverCapabilities.Read | DriverCapabilities.Write | DriverCapabilities.Browse,
        "地址即 NodeId 文本：ns=2;s=Channel1.Device1.Tag1（命名空间）或 i=2258（标准节点）。可用「浏览导入」从地址树选择。",
        "protocol-tutorials/opcua/01-overview");

    public string DriverId => DriverIdValue;
    public DriverDescriptor Descriptor => DescriptorValue;

    public IDriver Create(string connectionJson)
    {
        var options = JsonSerializer.Deserialize<OpcUaOptions>(
            string.IsNullOrWhiteSpace(connectionJson) ? "{}" : connectionJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new OpcUaOptions();
        return new OpcUaDriver(options);
    }
}
