# Sequences/Storage/ — 序列号存储实体

## 文件说明

| 文件 | 职责 |
|------|------|
| `TenE0Sequence.cs` | 序列号计数器实体：`Name`（序列名称）、`CurrentValue`（当前值）、`DatePart`（日期部分，用于重置判断） |
| `SequenceModelBuilderExtensions.cs` | EF Core 表映射：`(Name, DatePart)` 联合唯一索引 |
