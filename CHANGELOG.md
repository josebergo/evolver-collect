# 更新日志 / Changelog

本文件格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本遵循语义化版本（SemVer）。

## [1.0.0] - 2026-10-27

首个功能完整版本（M0~M5 全部里程碑交付）。

### 新增 — 南向采集（驱动插件化）
- **Modbus TCP / RTU / RTU-over-TCP**（自研帧层）：四种数据区、功能码 01/02/03/04/05/06/16、合并读优化（间隔≤8 合并、125 寄存器封顶）、异常码中文翻译、报文向量测试 + 模拟器端到端测试
- **OPC UA**（官方 .NET 栈）：NodeId 地址、批量读、地址空间浏览导入、匿名/用户名认证、自签证书处理
- **西门子 S7**（S7netplus）：S7-200 SMART/300/400/1200/1500，M/DB 区地址（M100/MW100/DB20.DBD4），ReadMultipleVars 分批
- **三菱 MC**（自研，Q/L/R 3E 帧）：D/W/M/L/B/X/Y 软元件、批读批写、结束码中文翻译
- **欧姆龙 FINS/TCP**（自研）：CIO/W/H/DM 区、位访问（字×16+位）、节点握手、结束码翻译
- **FANUC FOCAS**（独立 x86 适配器进程，stdio JSON 协议）：闭源 fwlib32 隔离、无真机自动 mock 模式演示
- 驱动参数 JSON Schema 自动渲染表单：新增驱动控制台零前端改动

### 新增 — 数据处理与可靠性
- 统一数据点（质量戳 Good/UNCERTAIN/Bad + 毫秒时间戳）
- 点位引擎：aX+b 工程量换算、位拆取、死区过滤、首值/质量变化必发
- 背压管道（有界 Channel，订阅者异常隔离）
- **断点续传**：SQLite 离线队列、发送失败转存、重连后按水位回放、容量上限 FIFO 淘汰
- 断线重连（指数退避）与整批 Bad 通告
- **报警引擎**：阈值/变化触发、触发/恢复双防抖、历史落库、SignalR 推送

### 新增 — 北向转发（多路并行）
- **MQTT**（MQTTnet 5）：{device} 主题模板按设备分主题、QoS、TLS、断线回放
- **HTTP POST**：自定义头/Content-Type
- **Kafka**（Confluent）：按设备 Key 分区保序
- **数据库**：MySQL 批量 INSERT（自动建表）/ InfluxDB v2 行协议
- 点位通配过滤（include/exclude）、json-lines/json-array/csv 格式、批量窗口

### 新增 — Web 控制台（Blazor + SignalR）
- 登录认证（PBKDF2 口令、首登强制改密、默认仅监听本机）
- 总览 Dashboard（通道状态/成功率/延迟/积压/丢弃计数/实时值）
- 通道/设备/点位管理、连通测试、写值下发、批量粘贴建点、**Excel 点表导入导出**
- 设备模板库（厂商+型号一键应用，示例模板 2 个）
- 报警规则与实时报警流
- **📚 内置文档中心**：15 篇教程（Modbus 从零/S7/MC/FINS/OPC UA/排障/驱动开发），嵌入 exe 完全离线，全文搜索

### 新增 — 工程化
- 模拟器：Modbus TCP/RTU-over-TCP 从站 + 虚拟数据发生器（无设备演示/测试）
- 98 个自动化测试（协议帧向量、引擎语义、管道背压、断点续传、报警、端到端）
- REST API（OpenAPI）+ SignalR Hub（/hubs/monitor）
- CI（Windows/Ubuntu 构建测试）、ADR 架构决策记录 ×5、插件 SDK 契约

### 已知限制
- OPC UA 为轮询模式（订阅推送在路线图）；S7 暂不支持 64 位与 String
- FOCAS 真机数据项需现场按 FANUC 手册校准（mock 模式可直接演示）
- v1.0 使用 EnsureCreated 建库（升级需重建，见 ADR-0003）
