# ADR-0005：插件机制——进程内注册优先，进程外走 stdio-json

- 状态：已接受（2026-10-27）

## 决策

- **进程内插件（默认）**：类库引用 `EvolverCollect.Core`，实现 `IDriverFactory`/`INorthboundFactory`，宿主注册一行。控制台按其 JSON Schema 自动渲染表单。简单、高效、类型安全。
- **进程外插件**：`evolver-plugin.json` 清单 + 独立可执行文件 + **stdio JSON 行协议**（与 FOCAS 适配器同一协议）。适用：闭源/特殊运行时（Python/Node 写驱动）、32 位库隔离、崩溃隔离。
- 未选择 gRPC：需要代码生成与额外端口管理；stdio 对工业现场更友好（可直接管道调试、无端口冲突）。

## 后果

- 进程外插件吞吐低于进程内（JSON 序列化 + 管道），适合中低频数据；
- 插件 SDK（`EvolverCollect.Plugins.Sdk`）先落清单契约；进程外宿主加载器在 v1.x 实现。
