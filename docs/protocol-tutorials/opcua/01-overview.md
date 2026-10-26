---
title: OPC UA 入门：信息模型与 NodeId
order: 1
---

# OPC UA 入门：信息模型与 NodeId

OPC UA（Unified Architecture）是 OPC 基金会制定的现代工业互联标准，比 Modbus "高级"在：自带类型系统、树形地址空间、订阅推送、安全证书。

## 地址空间：一棵树

设备的所有数据组织成一棵树：

```
Root
└─ Objects
   └─ 车间1
      └─ 1号机床
         ├─ 主轴负载    (Variable, 可读)
         ├─ 温度        (Variable)
         └─ 启停控制    (Variable, 可写)
```

每个节点有唯一 ID —— **NodeId**，两种常见形态：

| 形态 | 例子 | 说明 |
|---|---|---|
| 字符串 | `ns=2;s=Channel1.Device1.Tag1` | ns=命名空间号；s=字符串标识 |
| 数字 | `i=2258` | 标准节点（2258 = 服务器当前时间） |

**本系统的点位地址就是 NodeId 原文**，而且提供"浏览导入"：连上后直接从地址树勾选点位，不用手敲。

## 读 vs 订阅

- 读（本项目当前实现）：按周期 `Read` 节点值，与 Modbus 轮询一致
- 订阅（MonitoredItem）：值变化时服务器主动推送，实时性好（路线图中）

## 安全模式

- `None`：明文，内网常见
- `Sign`/`SignAndEncrypt`：基于 X.509 证书，首次连接要互信证书

现场大量自签名证书，所以参数里有"接受未受信证书"开关（默认开，公网环境请关闭）。

## 典型场景

- 新出厂设备（SIEMENS 828D、倍福、罗克韦尔）原生 UA Server
- 老系统 Kepware/上位机暴露 UA Server——把它当"协议汇聚器"接进来
