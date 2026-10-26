# ADR-0004：FOCAS 用独立 x86 适配器进程（stdio JSON 协议）

- 状态：已接受（2026-10-27）

## 背景

FANUC FOCAS 的 fwlib32.dll：32 位、闭源、**授权禁止随包分发**（用户须从 FANUC/随机光盘获取）。主程序面向 x64/跨平台。

## 决策

1. 独立进程 `evolver-focas-adapter`（x86），动态 `LoadLibrary("fwlib32.dll")`——主程序零引用闭源库，授权合规；
2. 通信协议：**stdio JSON 行协议**（connect/read/disconnect），比 gRPC 零依赖、可手工调试（直接 echo 一行 JSON 进管道）；
3. 无 fwlib32 时自动进入 **mock 模式**（仿真机床数据）——无真机也能演示完整链路；
4. 该协议同时被 ADR-0005 采纳为通用进程外插件协议（stdio-json）。

## 后果

- 多一层进程管理（驱动负责 spawn/kill/超时）；
- 真机数据项（cnc_statinfo/rdprognum 等）结构体布局需现场按 FANUC 手册校准（代码中已留注释位）。
