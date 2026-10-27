# Evolver Collect

> A lightweight, open-source industrial data collection gateway that helps manufacturers get device data collected and forwarded in 10 minutes — by configuration, not code.
> 帮制造企业用"配置"而不是"写代码"，10 分钟把设备数据采上来、转出去。

**English** ｜ [简体中文](./README.zh-CN.md)

<div align="center">

**.NET 10** · **Blazor** · **SQLite** · **Apache-2.0**

[Quick Start](#quick-start) · [Features](#features) · [Architecture](#architecture) · [Docs](docs/) · [Contributing](CONTRIBUTING.md)

</div>

## Why this project exists

For any manufacturer, the first step of digitalization is always "get the data off the machines" — and it hurts: a zoo of protocols (Modbus / OPC UA / S7 / Mitsubishi / Omron…), expensive commercial software (Kepware), open-source projects that assume Linux and containers, and shop-floor PCs that run Windows on an isolated network.

**Evolver Collect's answer**: a single self-contained executable with a built-in web console. Create a channel → import a device template → tags come along automatically → one-click connectivity test → start collecting → forward to MQTT / Kafka / HTTP / databases. Reconnection, data-quality stamps, and local buffering with store-and-forward are all built in.

## Features

- **Southbound collection (pluggable drivers)**: Modbus TCP/RTU, OPC UA (address-space browsing & import), Siemens S7, Mitsubishi MC, Omron FINS, FANUC FOCAS (separate adapter process + mock mode for demos)
- **Tag engine**: raw → engineering value (aX+b scaling), bit extraction, deadband filtering, quality stamps (Good/Uncertain/Bad)
- **Northbound forwarding**: MQTT (`{device}` topic templates) / HTTP / Kafka / databases (MySQL, InfluxDB), wildcard tag filters, multiple destinations in parallel
- **Reliability**: exponential-backoff reconnection, SQLite offline queue with store-and-forward (configurable capacity, FIFO eviction), alarm engine (threshold/change triggers with trigger & recovery debounce)
- **Web console**: real-time Blazor dashboard (channel status / live values / throughput / backlog depth), Excel tag import & export, device template library, write-back to devices
- **📚 Built-in documentation center**: protocol tutorials (Modbus/S7/MC/FINS/OPC UA…), troubleshooting handbook, driver development guide — fully offline inside the app. **The tool itself is the textbook.**
- **Security**: accounts with forced password change on first login, localhost-only by default, PBKDF2 password hashing, authenticated APIs
- **Quality**: 98 automated tests (protocol frame vectors / end-to-end / backpressure / store-and-forward), plus a bundled Modbus simulator so you can demo without hardware

## Architecture

```
┌──────────────────────────────────────────────────────────────────┐
│ Management   Web console (Blazor + SignalR) ＋ 📚 built-in docs  │
├──────────────────────────────────────────────────────────────────┤
│ Interfaces   REST API (Minimal API + OpenAPI) │ SignalR Hub      │
├──────────────────────────────────────────────────────────────────┤
│ Northbound   MQTT │ HTTP │ Kafka │ Databases                     │
├──────────────────────────────────────────────────────────────────┤
│ Processing   Tag engine (scaling/deadband) · Alarms ·            │
│              Backpressured pipeline · Offline queue              │
├──────────────────────────────────────────────────────────────────┤
│ Southbound   Pluggable driver framework                          │
│   Modbus TCP/RTU │ OPC UA │ S7 │ MC │ FINS │ FOCAS (adapter)    │
├──────────────────────────────────────────────────────────────────┤
│ Storage      SQLite (config + offline queue + alarm history)     │
│              = zero external dependencies                        │
└──────────────────────────────────────────────────────────────────┘
```

Architecture Decision Records (why Blazor, why a hand-rolled Modbus stack, why FOCAS runs in a separate process…): [docs/adr/](docs/adr/)
Release history: [CHANGELOG.md](CHANGELOG.md)

## Quick Start

Prerequisite: .NET 10 SDK (release builds are self-contained — no dependencies at all).

```bash
git clone https://github.com/josebergo/evolver-collect.git
cd evolver-collect
dotnet run --project src/EvolverCollect.Server
# Open http://127.0.0.1:5080 (default login admin / admin — you must change it on first sign-in)
```

No real hardware? No problem — the project ships with a simulator (Modbus TCP slave + data generator):

```bash
dotnet run --project simulator/EvolverCollect.Simulator   # starts a Modbus TCP slave on :1502
```

Then create a channel in the console (driver `modbus-tcp`, host 127.0.0.1, port 1502), add a tag `40001` (Float),
watch live values on the dashboard — and follow along frame by frame in the built-in docs center ("Modbus from Zero").

## Development

```bash
dotnet build
dotnet test          # protocol frame replay / pipeline / engine tests
```

- Layered solution: `Core` (abstractions + pipeline + runtime, zero protocol dependencies) ← `Storage` (EF Core/SQLite) ← `Server` (host)
- Drivers live in `src/EvolverCollect.Drivers.*` — implement `IDriver`/`IDriverFactory` and everything self-registers; the console renders its config form automatically from the driver's JSON Schema
- Read the ADRs under `docs/adr/` before making architectural changes

## Roadmap

v1.0 delivers the full core capability set (see [CHANGELOG](CHANGELOG.md)). Up next: OPC UA subscriptions, the out-of-process plugin loader, EF Core migrations, Chinese tokenizer for doc search, and a Gitee mirror.

## License

[Apache-2.0](LICENSE) · Proprietary SDKs such as FANUC FOCAS (fwlib32.dll) must be obtained from the vendor by the user; this project does not redistribute them.
