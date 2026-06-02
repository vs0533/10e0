# 15 — 序列号生成器

## 架构概览

```
[Sequence("order", "ORD-{yyyyMMdd}-{0000}")]
public string OrderNumber { get; set; } = "";

         │ EntityService.CreateAsync 检测到 [Sequence] 属性
         ▼
ISequenceGenerator.NextAsync(key, format)
         │
         ▼
EfSequenceGenerator<TContext>
   │  1. SequenceFormat.Parse(format) 解析格式串
   │  2. 推导 bucket，判断是否需要归零
   │  3. TenE0Sequence 表乐观并发递增（最多 5 次重试）
   ▼
返回 "ORD-20260518-0001"
```

## ISequenceGenerator

```csharp
public interface ISequenceGenerator
{
    Task<string> NextAsync(
        string sequenceKey,
        string format,
        CancellationToken cancellationToken = default);
}
```

- **`sequenceKey`**：业务 key，区分不同序列空间，如 `"order"`、`"course_code"`、`"invoice"`。
- **`format`**：格式模板，混合字面量 + 日期占位 + 序号占位。

## 格式语法

格式串由三段自由组合：

| 占位 | 语法 | 说明 |
|------|------|------|
| 字面量 | 任意文本 | 原样输出，如 `ORD-`、`INV-` |
| 日期 | `{yyyyMMdd}` | .NET 标准日期格式串，支持 `yyyyMMdd` / `yyyyMM` / `yyyy` 等 |
| 序号 | `{0000}` | N 个 `0` 表示宽度，自动补零 |

**限制**：每个格式串最多一个日期占位、一个序号占位。

### 示例

| 模板 | Bucket 类型 | 输出示例 |
|------|------------|----------|
| `ORD-{yyyyMMdd}-{0000}` | 日重置 | `ORD-20260518-0001` → `ORD-20260518-0002` →（次日）`ORD-20260519-0001` |
| `INV-{yyyyMM}-{00000}` | 月重置 | `INV-202605-00001` → `INV-202605-00002` →（次月）`INV-202606-00001` |
| `DOC-{yyyy}-{000000}` | 年重置 | `DOC-2026-000001` → `DOC-2026-000002` →（次年）`DOC-2027-000001` |
| `USR{00000000}` | 永不重置 | `USR00000001` → `USR00000002` → `USR00000003` ... |

## Bucket 与重置行为

Bucket 由格式串中的日期部分决定—`SequenceFormat.RenderBucket` 将当前时间按日期格式渲染为 bucket 字符串，`EfSequenceGenerator` 对比 `CurrentBucket` 判断是否需要归零：

| 日期占位 | Bucket 值 | 重置频率 |
|----------|-----------|----------|
| `{yyyyMMdd}` | `20260518` | 每天 |
| `{yyyyMM}` | `202605` | 每月 |
| `{yyyy}` | `2026` | 每年 |
| 无日期 | `_`（固定值） | 永不重置 |

## TenE0Sequence 表

存储序列计数器的数据库表，由框架自动管理：

| 列名 | 类型 | 说明 |
|------|------|------|
| `SequenceKey` | string(64) | 业务 key，唯一索引 |
| `CurrentBucket` | string(32) | 当前 bucket，用于判定归零 |
| `CurrentNumber` | long | 当前已分配的序号值 |

## EfSequenceGenerator 实现

基于 EF Core 的并发安全实现：

```csharp
public sealed class EfSequenceGenerator<TContext> : ISequenceGenerator
    where TContext : DbContext
```

**并发策略**：乐观并发控制 + 最多 5 次重试。

1. 查询 `TenE0Sequence` 记录（按 `SequenceKey` 唯一索引定位）
2. 不存在 → 首次分配，插入 `CurrentNumber=1`
3. 存在且 bucket 匹配 → `CurrentNumber += 1`
4. 存在但 bucket 不匹配 → 归零：`CurrentNumber=1`、`CurrentBucket=新值`
5. `SaveChangesAsync` 捕获 `DbUpdateConcurrencyException` 或唯一键冲突 → 随机延迟 5-30ms 后重试，最多 5 次
6. 超过重试上限 → 抛出 `InvalidOperationException`

```csharp
// 重试逻辑核心
for (var attempt = 0; attempt < MaxRetries; attempt++)
{
    try
    {
        return SequenceFormat.Render(parsed, await IncrementAsync(...), now);
    }
    catch (DbUpdateConcurrencyException) when (attempt < MaxRetries - 1)
    {
        await Task.Delay(Random.Shared.Next(5, 30), cancellationToken);
    }
}
```

## [Sequence] 属性标注

通过 `[Sequence]` 特性标记实体属性，`EntityService.CreateAsync` 自动填充：

```csharp
[AttributeUsage(AttributeTargets.Property)]
public sealed class SequenceAttribute(string sequenceKey, string format) : Attribute;

// 使用示例
public sealed class Order : AuditedEntity
{
    [Sequence("order", "ORD-{yyyyMMdd}-{0000}")]
    public string OrderNumber { get; set; } = "";

    public required string Title { get; set; }
}
```

**行为规则**：
- Create 时属性为空 → 自动调用 `NextAsync` 填充
- Create 时属性非空 → 保留客户端传入值，不重新生成
- Update 始终不重新生成（流水号一旦分配不应变更）
- 字段必须是 `string` 类型

## DI 注册

```csharp
// Program.cs
builder.Services.AddTenE0Sequences<AppDbContext>();
```

内部注册 `ISequenceGenerator` 为 Scoped 生命周期，指向 `EfSequenceGenerator<TContext>`。

`TenE0Sequence` 表由 `TenE0SystemDbContext` 自动注册—业务 DbContext 无需额外配置。

## 设计决策

- **为什么不用 SQL Server SEQUENCE**：无法跨数据库 provider 兼容（需同时支持 SQLite、MySQL、Postgres），且 SQL SEQUENCE 不支持按日期自动归零。
- **为什么不用 MAX+1**：旧版 `GenerateDBNumber` 查 MAX 值拼接，非并发安全。新版用行级原子递增 + 重试，业务层面安全。
- **高并发场景**：可在 `IncrementAsync` 中加行锁（SQL Server `UPDLOCK`）或改用数据库原生 SEQUENCE + 前缀拼接，本实现默认乐观重试已满足绝大多数业务场景。

## 注意事项

- `TenE0Sequence` 表记录由框架自动维护，不应手动修改 `CurrentNumber` 值。
- 格式串中日期占位和序号占位各只能出现一次，否则 `SequenceFormat.Parse` 会抛出 `ArgumentException`。
- 修改 `[Sequence]` 的 `format` 参数后，已有数据的 bucket 可能不匹配，下个周期会自动归零，无需手动迁移。
