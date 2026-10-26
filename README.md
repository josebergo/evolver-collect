# Evolver Collect

> 帮制造企业用"配置"而不是"写代码"，在 10 分钟内把设备数据采上来、转出去的开源软件。
> A lightweight, open-source industrial data collection gateway for manufacturers — configure, don't code.

<div align="center">

**.NET 10** · **Blazor** · **SQLite** · **Apache-2.0**

[快速开始](#快速开始) · [功能](#功能) · [架构](#架构) · [内置文档中心](docs/adr/0001-tech-stack-net10-blazor-sqlite.md) · [参与贡献](CONTRIBUTING.md)

</div>

## 为什么做这个

生产企业数字化的第一步永远是"把设备数据采上来"，而这一步最痛：协议杂（Modbus/OPC UA/S7/三菱/欧姆龙…）、
商业软件贵（Kepware）、开源项目偏 Linux/容器、现场工控机多为 Windows 内网环境。

**Evolver Collect 的答案**：一个绿色版单文件程序，内置 Web 控制台，向导式配置通道 → 导入设备模板 → 点位自动带出 → 一键连通测试 → 启动采集 → 转发 MQTT/Kafka/HTTP/数据库。断线重连、数据质量标记、本地缓存断点续传全部内置。

## 功能

- **南向采集（驱动插件化）**：Modbus TCP/RTU、OPC UA（浏览导入）、西门子 S7、三菱 MC、欧姆龙 FINS、FANUC FOCAS（独立适配器 + mock 演示模式）
- **点位引擎**：地址 → 工程量换算（aX+b）、位拆取、死区过滤、质量戳（Good/Uncertain/Bad）
- **北向转发**：MQTT（{device} 主题模板）/ HTTP / Kafka / 数据库（MySQL、InfluxDB），按点位通配过滤、多路分发
- **可靠性**：指数退避断线重连、SQLite 离线队列断点续传（可配容量、FIFO 淘汰）、报警引擎（阈值/变化 + 双防抖）
- **控制台**：Blazor 实时监控（通道状态/实时值/吞吐/积压深度）、Excel 点表导入导出、设备模板库、写值下发
- **📚 内置文档中心**：协议教程（Modbus/S7/MC/FINS/OPC UA…）、排障手册、驱动开发指南，完全离线可查——**工具本身就是教材**
- **安全**：账号体系 + 首登强制改密、默认只监听本机、口令 PBKDF2 落盘、全部 API 需认证
- **质量**：98 个自动化测试（协议帧向量 / 端到端 / 背压 / 断点续传），自带 Modbus 模拟器无设备可演示

## 架构

```
┌─────────────────────────────────────────────────────────────┐
│ 管理层  Web 控制台（Blazor + SignalR 实时）＋📚 内置文档中心  │
├─────────────────────────────────────────────────────────────┤
│ 接口层  REST API（Minimal API + OpenAPI）│ SignalR Hub       │
├─────────────────────────────────────────────────────────────┤
│ 北向分发层  MQTT │ HTTP │ Kafka │ 数据库                     │
├─────────────────────────────────────────────────────────────┤
│ 数据处理层  点位引擎(换算/死区) · 报警 · 背压管道 · 离线队列  │
├─────────────────────────────────────────────────────────────┤
│ 南向采集层  驱动框架（插件化）                                │
│   Modbus TCP/RTU │ OPC UA │ S7 │ MC │ FINS │ FOCAS(适配器)  │
├─────────────────────────────────────────────────────────────┤
│ 存储  SQLite（配置 + 离线队列 + 报警历史）＝ 零外部依赖       │
└─────────────────────────────────────────────────────────────┘
```

详细设计：[docs/proposal.zh-CN.md](docs/proposal.zh-CN.md)（项目方案 v0.2）｜架构决策记录：[docs/adr/](docs/adr/)

## 快速开始

前置：.NET 10 SDK（运行发布版则无需任何依赖）。

```bash
git clone https://github.com/<you>/evolver-collect.git
cd evolver-collect
dotnet run --project src/EvolverCollect.Server
# 打开 http://127.0.0.1:5080 （默认账号 admin / admin，首登强制修改）
```

无真实设备也能玩：项目自带模拟器（Modbus TCP 从站 + 数据发生器）——

```bash
dotnet run --project simulator/EvolverCollect.Simulator   # 起一个 Modbus TCP 从站 :1502
```

然后在控制台新建通道（驱动选 `modbus-tcp`，IP 127.0.0.1、端口 1502），建点位 `40001`（Float），
Dashboard 即可看到实时数据，并在"文档中心 → Modbus 从零"里对照学习每一帧报文的含义。

## 开发

```bash
dotnet build
dotnet test          # 协议帧回放测试 / 管道测试 / 引擎测试
```

- 解决方案分层：`Core`（抽象+管道+运行时，零协议依赖）← `Storage`（EF Core/SQLite）← `Server`（宿主）
- 驱动项目：`src/EvolverCollect.Drivers.*`，实现 `IDriver`/`IDriverFactory` 即自动注册，控制台按驱动的 JSON Schema 自动渲染配置表单
- 架构决策记录（ADR）在 `docs/adr/`，动手前先读

## 路线

v0.1 Modbus+MQTT+控制台 → v0.2 文档中心+OPC UA+模板库 → v0.3 S7/三菱/欧姆龙+报警+Kafka → v0.4 CNC(FOCAS)+数据库北向 → v1.0 插件 SDK+压测。详见 [方案 §17](docs/proposal.zh-CN.md)。

## License

[Apache-2.0](LICENSE) · FANUC FOCAS 等闭源 SDK 由使用者自行向厂商获取，本项目不随包分发。
