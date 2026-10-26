# ADR-0001：技术栈与整体架构选型

- 状态：已接受（2026-10-26）
- 关联方案：docs/proposal.zh-CN.md §6/§12

## 背景

项目需要同时满足：工业现场部署（Windows 工控机为主、内网、无 Docker）、单人业余开发可持续维护、
开源社区可贡献、并作为 .NET 方向的求职作品。

## 决策

| 决策点 | 选择 | 理由 |
|---|---|---|
| 运行时 | .NET 10（LTS，支持至 2028） | 长支持窗口覆盖项目周期；性能与 API 面成熟 |
| 后端框架 | ASP.NET Core Minimal API | 轻量、显式、单文件部署友好 |
| 控制台 | Blazor（Interactive Server） | 单语言全栈；SignalR 实时刷新为内置能力；单人维护成本最低 |
| 存储 | EF Core + SQLite（单库） | 配置库/离线队列/报警历史一体；零外部依赖（工控现场不能假设有数据库服务） |
| 部署 | 单文件自包含发布（win-x64 / linux-x64 / arm64） | "拷贝即部署"是目标用户硬需求 |
| 仓库 | monorepo（src/ drivers/ tests/ docs/ templates/ simulator/） | 驱动多但同构，统一构建与版本 |

## 被否决的方案

- **Go + Vue**：部署同样优秀，但作者主攻 .NET 求职，作品集需要 .NET 深度（Odin 生态参考 Neuron）。
- **前后端分离（Vue3/React）**：单人维护两套技术栈成本高；Blazor 已覆盖实时场景；REST API 天然保留后续分离的可能。
- **PostgreSQL/TimescaleDB 等外部数据库**：违背"拷贝即部署"；时序存储交给北向对接（InfluxDB/TDengine）。

## 后果

- Blazor Server 模式下控制台与采集进程必须同机（本地工具场景天然成立；远程多客户端是后续插件化部署的话题）。
- 全栈 C# 的代价是前端生态表现力弱于 Vue，接受（工具属性 > 营销属性）。
