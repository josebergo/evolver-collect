# ADR-0002：Modbus 帧层自研（不用 FluentModbus）

- 状态：已接受（2026-10-27）
- 关联：docs/proposal.zh-CN.md §12（方案原计划用 FluentModbus）

## 背景

方案 v0.2 的选型表里 Modbus 计划用 FluentModbus。实施时改为自研帧层。

## 决策

自研 `ModbusCodec`（MBAP/RTU 编解码 + CRC16）+ `ReadPlanner`（合并读）+ 传输层，理由：

1. **学习目标**：作者要借项目吃透工业协议，Modbus 是第一课——用库学不到帧级知识
2. **合并读可控**：FluentModbus 的批量读策略不可定制，自研可按"间隔 ≤8 合并、125 寄存器封顶"优化
3. **异常语义贴合**：从站异常码直接翻译成中文点位级 Bad 原因，链路错误精确区分
4. **教学/产品双赢**：报文级测试（官方样例向量）成为项目质量招牌；文档中心《Modbus 从零》与代码互为印证

## 后果

- 维护成本自担：需用样例帧向量 + 模拟器端到端测试兜底（已建立，见 `ModbusCodecTests`/`ModbusDriverEndToEndTests`）
- S7 采用 S7netplus（协议复杂度高一个量级，且该库 MIT 成熟）——每个协议按复杂度权衡"自研 vs 用库"
