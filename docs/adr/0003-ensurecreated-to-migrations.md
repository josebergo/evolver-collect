# ADR-0003：v0.1 使用 EnsureCreated，v1.0 前切换 EF Core 迁移

- 状态：已接受（2026-10-26）

## 背景

v0.1 阶段实体结构仍在快速演进，频繁生成迁移文件会制造大量噪音提交；且 EnsureCreated 对首次部署体验最简单（开箱建库）。

## 决策

- v0.1：`Database.EnsureCreated()` 建库 + 种子（见 `DatabaseInitializer`）。
- v0.3 起引入 EF Core 迁移：先在开发库上生成基线迁移（`dotnet ef migrations add Baseline`），发布文档说明"旧库需重新初始化或执行重建脚本"。
- v1.0：发布升级文档，保证老库配置无损升级。

## 后果

- v0.1~v0.2 期间升级版本需要删除 data/evolver.db 重建（文档与 Release Note 明确提示）。
