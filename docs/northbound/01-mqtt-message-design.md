---
title: MQTT 报文设计与 QoS 选择
order: 1
---

# MQTT 报文设计与 QoS 选择

## 默认报文格式

北向选 MQTT 时，每个数据点序列化为一行 JSON（json-lines）：

```json
{"device":"CNC-01","tag":"spindle_load","value":42.5,"quality":"Good","timestamp":1798300800123}
```

| 字段 | 类型 | 说明 |
|---|---|---|
| device | string | 设备名 |
| tag | string | 点位名 |
| value | any | 工程量（换算后） |
| quality | string | Good/UNCERTAIN/Bad |
| timestamp | long | 毫秒时间戳（采集侧） |

批量窗口内多个点合并成多行报文（或切 json-array/csv 格式）。

## 主题设计

- 单主题汇聚：`evolver-collect/data`
- **按设备分主题**：主题模板写 `factory/{device}`，系统自动按设备拆分发布——下游按机床订阅，互不干扰
- 回放积压统一发到 `{device}=spool` 子主题，下游可识别补数

## QoS 怎么选

| QoS | 语义 | 适用 |
|---|---|---|
| 0 | 至多一次（可能丢） | 高频遥测、允许小概率丢失 |
| 1 | 至少一次（可能重） | 一般生产数据（默认） |

配合本项目**本地离线队列**，QoS0 也能做到断网不丢——网关先把数据落盘，重连后回放。

## 对接 EMQX/Mosquitto 快速验证

```bash
mosquitto_sub -h 127.0.0.1 -t 'evolver-collect/#' -v
```

能滚动看到 JSON 行即通。TLS/账号密码在北向配置表单里填。
