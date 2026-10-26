---
title: 如何编写一个新驱动
order: 1
---

# 如何编写一个新驱动

本项目的驱动 = 一个实现 `IDriver`/`IDriverFactory` 的类库。控制台按驱动声明的 JSON Schema **自动渲染配置表单**——新驱动零前端代码。

## 最小骨架

```csharp
// 1. 连接参数 POCO（也用于反序列化通道的 ConnectionJson）
public sealed class MyOptions { public string Host { get; set; } = "127.0.0.1"; }

// 2. 工厂：声明元数据（关键！Schema 决定界面表单）
public sealed class MyDriverFactory : IDriverFactory
{
    public static DriverDescriptor DescriptorValue { get; } = new(
        "my-protocol", "我的协议", "XX 厂商",
        """
        { "type": "object",
          "properties": { "host": { "type": "string", "title": "IP 地址" } },
          "required": ["host"] }
        """,
        DriverCapabilities.Read | DriverCapabilities.Write,
        AddressHelp: "地址语法说明（显示在配置界面）",
        DocsSlug: "protocol-tutorials/my/01-overview");

    public string DriverId => "my-protocol";
    public DriverDescriptor Descriptor => DescriptorValue;
    public IDriver Create(string connectionJson) =>
        new MyDriver(JsonSerializer.Deserialize<MyOptions>(connectionJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new());
}

// 3. 驱动：实现 5 个方法
public sealed class MyDriver(MyOptions options) : IDriver
{
    public DriverDescriptor Descriptor => MyDriverFactory.DescriptorValue;
    public DriverStats Stats { get; } = new();

    public async Task<ConnectResult> ConnectAsync(CancellationToken ct) { /* 建链+握手 */ }
    public Task<IReadOnlyList<DataPoint>> ReadAsync(DeviceBatch batch, CancellationToken ct)
    {
        // 成功 → Good 点；从站拒绝 → 该点 Bad；链路级故障 → 抛 IOException（运行时负责重连）
    }
    public Task<WriteResult> WriteAsync(...) { }
    public ITagBrowser? CreateTagBrowser() => null;   // 支持浏览则实现
    public Task DisconnectAsync() { }
    public ValueTask DisposeAsync() { }
}
```

## 契约要点（务必遵守）

1. **失败语义**：`ModbusException` 式（明确拒绝）→ 相关点 Bad 继续；`IOException` 式（链路断）→ 抛出让运行时统一重连
2. **Batch.StationNo** 是设备从站号，多从站共享连接时按它路由
3. **DriverStats** 每次读写都要记录（界面成功率/延迟来源）
4. 地址解析错误 → 该点 Bad 带原因，不拖垮其他点

## 注册

宿主 `Program.cs`：

```csharp
builder.Services.AddSingleton<IDriverFactory, MyDriverFactory>();
```

界面自动出现该驱动类型。

## 测试要求

- 帧构造/解析：录制真实报文做向量测试（参考 `ModbusCodecTests`）
- 端到端：对模拟器或设备跑读写用例（参考 `ModbusDriverEndToEndTests`）

提交 PR 前读 `CONTRIBUTING.md`。
