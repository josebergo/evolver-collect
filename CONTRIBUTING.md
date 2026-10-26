# 参与贡献

欢迎 Issue / PR / 文档 / 设备模板！低门槛入口见下文"设备模板贡献"。

## 开发环境

- .NET 10 SDK
- `dotnet build && dotnet test`

## 提交 PR 前

1. `dotnet build` 与 `dotnet test` 全绿；
2. 新驱动/北向必须有对应单元测试（协议帧类请提供"录制报文→解析断言"的用例）；
3. 涉及架构取舍的变更请先在 `docs/adr/` 补一份 ADR 并在 Issue 里讨论。

## 编写一个新驱动（最短路径）

1. 新建 `src/EvolverCollect.Drivers.<协议>` 项目，引用 `EvolverCollect.Core`；
2. 实现 `IDriverFactory`（`DriverId` 唯一、`Descriptor.ParameterSchemaJson` 描述连接参数）与 `IDriver`；
3. 在宿主 `Program.cs` 注册工厂——控制台即可自动出现该驱动类型，配置表单按 JSON Schema 自动渲染；
4. 参考 `docs/developer/driver-guide.md`（工具内置文档中心可查）。

## 设备模板贡献（good first issue）

把你现场验证过的设备点表整理为 `templates/<厂商>/<型号>.json`（含 Vendor/Model/点位列表），
附一张设备型号照片或手册截图链接即可。这是对社区最有价值、门槛最低的贡献。

## 行为准则

友善、专业、对事不对人。
