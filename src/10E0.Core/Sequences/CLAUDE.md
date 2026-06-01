# Sequences/ — 序列号生成器

数据库支撑的自增序列号生成，支持按日期重置。

## 文件说明

| 文件 | 职责 |
|------|------|
| `ISequenceGenerator.cs` | 序列号生成器接口 |
| `EfSequenceGenerator.cs` | EF Core 实现：从 `TenE0Sequence` 表读取/更新计数器，支持并发安全 |
| `SequenceAttribute.cs` | 属性标注：`[Sequence("DEMO-{yyyyMMdd}-{0000}")]`，EntityService.CreateAsync 自动填充 |
| `SequenceFormat.cs` | 格式解析工具：将模板字符串解析为前缀 + 日期格式 + 序号长度 |

## 格式示例

| 模板 | 生成结果 |
|------|----------|
| `DEMO-{yyyyMMdd}-{0000}` | `DEMO-20260601-0042` |
| `ORD-{yyyyMM}-{00000}` | `ORD-202606-00001` |
| `INV-{0000}` | `INV-0001`（无日期部分，永不重置） |

## 重置逻辑

序列号按日期部分自动重置。例如 `DEMO-{yyyyMMdd}-{0000}` 每天从 0001 重新开始。无日期部分的序列永不重置。

## 对比旧版

- 旧版 `GenerateDBNumber` 查 MAX 值 + 拼接，非并发安全
- 新版 `EfSequenceGenerator` 使用数据库行锁 + 原子递增，并发安全

## 子目录

| 目录 | 职责 |
|------|------|
| `Storage/` | 序列号存储实体 + EF 映射 |
