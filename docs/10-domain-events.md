# 10. 领域事件与 Outbox Pattern

领域事件（Domain Events）是 DDD 中解耦聚合的核心机制。10E0 基于 **Outbox Pattern** 提供了一套完整的事件基础设施，确保事件与业务数据在同一事务中原子持久化，再由后台 Relay 异步投递。

## 10.1 快速概览

整个事件流分三步，全部自动完成：

```
业务方法 Raise 事件  →  OutboxInterceptor 原子持久化  →  OutboxRelay 后台投递
```

## 10.2 定义领域事件

事件必须是 **不可变 record**，实现 `IDomainEvent` 标记接口，命名用**过去式**，且携带处理所需的**全部上下文**（自包含原则，handler 不应再查库补全数据）。

```csharp
using TenE0.Core.Events;

// ✅ 过去式命名，自包含上下文
public sealed record OrderCreatedEvent(
    string OrderId,
    string CustomerId,
    decimal TotalAmount
) : IDomainEvent;

public sealed record OrderPaidEvent(
    string OrderId,
    DateTimeOffset PaidAt,
    string PaymentMethod
) : IDomainEvent;
```

> **重要**：事件通过 JSON 序列化存入 Outbox 表（`System.Text.Json`），确保所有字段类型可序列化。

## 10.3 触发事件

事件通过聚合根内部的 `Raise()` 方法发出（`protected`，仅聚合内部可调用）：

```csharp
public class Order : AggregateRoot
{
    public string CustomerId { get; set; } = "";
    public decimal Amount { get; set; }
    public bool IsPaid { get; private set; }

    public void Pay(string paymentMethod)
    {
        if (IsPaid)
            throw new InvalidOperationException("订单已支付");

        IsPaid = true;
        Raise(new OrderPaidEvent(Id, TimeProvider.System.GetUtcNow(), paymentMethod));
    }
}
```

`Raise()` 将事件加入 `AggregateRoot` 内部列表（`[NotMapped]`，不映射到数据库），等到 `SaveChangesAsync` 时统一处理。

## 10.4 处理事件

### 定义 Handler

实现 `IDomainEventHandler<T>`，一个事件可以有多个 handler（fan-out）：

```csharp
public sealed class OrderPaidNotifyHandler : IDomainEventHandler<OrderPaidEvent>
{
    public Task HandleAsync(OrderPaidEvent evt, CancellationToken ct)
    {
        // 发送通知...
        return Task.CompletedTask;
    }
}

public sealed class OrderPaidAuditHandler : IDomainEventHandler<OrderPaidEvent>
{
    public Task HandleAsync(OrderPaidEvent evt, CancellationToken ct)
    {
        // 写审计日志...
        return Task.CompletedTask;
    }
}
```

> **必须幂等**：消息可能重复投递（如 Relay 进程重启），handler 必须自行判断状态，避免重复副作用。

### Fan-out 容错

InProcessDomainEventDispatcher 逐个调用 handler，**单个失败不阻断其他 handler**。所有 handler 执行完后，如有失败则抛出 `AggregateException`。

## 10.5 DI 注册

```csharp
// 注册 Outbox 基础设施（泛型 TContext 绑定具体 DbContext 类型）
builder.Services.AddTenE0DomainEvents<AppDbContext>(opt =>
{
    opt.BatchSize = 50;                      // 每轮投递最大消息数
    opt.PollInterval = TimeSpan.FromSeconds(2);  // 轮询间隔
    opt.MaxAttempts = 8;                     // 毒消息最大重试次数
    opt.LockLeaseDuration = TimeSpan.FromSeconds(30);  // 行级锁租约时长
    opt.LockProvider = OutboxLockProviderKind.RowLock;  // 0/1 实例可省（None）
});

// 扫描程序集，注册所有 IDomainEventHandler<T> 实现
builder.Services.AddTenE0DomainEventHandlersFromAssembly(typeof(Program).Assembly);

// 毒消息管理（可选）：暴露 IOutboxAdmin 给运维 endpoint / 脚本
builder.Services.AddTenE0OutboxAdmin<AppDbContext>();
```

### 10.5.1 多实例部署选择 Lock Provider

`OutboxLockProviderKind` 决定多实例 Relay 怎么避免竞争同一行：

| 部署形态 | 推荐配置 | 说明 |
|----------|----------|------|
| 单实例 | `None`（默认） | 0 竞争，无需锁 |
| 同库多实例（SqlServer / PG） | `RowLock` | 按 `Database.ProviderName` 自动选 `SqlServerOutboxLock` / `PostgresOutboxLock` |
| 跨库 / 不想改表结构 | `Distributed` | 需先注册 `IMultiLevelCache`（L2 用 Redis） |
| 想"全局唯一 Relay" | `Leader` | `LeaderElector` 抢主，仅 leader 实例跑投递 |

详见 `src/10E0.Core/Events/Outbox/CLAUDE.md` 的"多实例安全"小节。

## 10.6 完整三步流程

### 第一步：Raise — 内存积累

业务方法调用 `Raise()`，事件存入 `AggregateRoot._pendingEvents` 列表（`NotMapped`，零数据库 IO）。

### 第二步：OutboxInterceptor — 原子持久化

`SaveChangesAsync()` 触发 EF Core 拦截器 `OutboxInterceptor.SavingChangesAsync`：

1. 扫描 `ChangeTracker.Entries<AggregateRoot>()`，找出有 `PendingEvents` 的聚合
2. 逐个序列化事件为 JSON（`AssemblyQualifiedName` 记录 CLR 类型）
3. `context.Add(new OutboxMessage { EventType, Payload, OccurredOn })`
4. `aggregate.ClearEvents()` 清空内存列表
5. EF Core 在同一事务中提交：**业务数据变更 + OutboxMessage 插入 = 原子提交**

> 💡 **事件作用域**：事件的 `Raise` → `PendingEvents` 生命周期绑定到跟踪该聚合的 `DbContext` 实例。确保 `Raise` 和 `SaveChanges` 使用同一个 DbContext，否则事件将丢失。

### 第三步：OutboxRelay — 后台异步投递

`OutboxRelayService<TContext>`（`BackgroundService`）持续轮询 Outbox 表：

- 查询条件：`SentTime IS NULL AND AttemptCount < MaxAttempts`
- 按 `OccurredOn` 排序，分批次取出
- 每条消息：`AttemptCount++` → `IOutboxPublisher.PublishAsync()` → 成功则 `SentTime = now`，失败则 `LastError = 异常信息(截断2000字符)`
- 空闲时等待 `PollInterval` 避免空转

## 10.7 毒消息处理（Poison Message）

查询条件 `AttemptCount < MaxAttempts`（默认 8 次）是毒消息滤网。超过 `MaxAttempts` 的消息：
- **不再被 Relay 拾取**
- **永远保留在 Outbox 表中**（`SentTime` 仍为 NULL）
- 框架内置 `IOutboxAdmin` 三操作供运维消费：
  - `GetPoisonMessagesAsync()` — 列出所有毒消息
  - `RetryPoisonMessageAsync(id)` — 重置（`AttemptCount=0`, `LastError=null`），下轮 Relay 重新拾取
  - `ExportPoisonMessagesAsync()` — 导出为 `OutboxPoisonMessageDto`（Id / EventType / Payload / OccurredOn / AttemptCount / LastError）供离线分析

注册：`builder.Services.AddTenE0OutboxAdmin<AppDbContext>()`，然后在 endpoint / 定时任务 / 管理后台消费。

## 10.8 可插拔发布器

默认 `InProcessOutboxPublisher` 反序列化 JSON 后分发到本进程 handler。替换投递机制只需实现 `IOutboxPublisher` 并用 DI 替换：

```csharp
// 替换为 Kafka 发布器
services.Replace(ServiceDescriptor.Scoped<IOutboxPublisher, KafkaOutboxPublisher>());
```

切换后 Relay 仍正常工作，业务 handler 代码零改动。

## 10.9 重要注意事项

| 要点 | 说明 |
|------|------|
| **幂等性** | handler 必须幂等，Relay 重启可能导致消息重复投递 |
| **不可变性** | 事件 record 的所有属性必须是 `init` 或只读字段 |
| **自包含上下文** | handler 不应查库补全数据，事件本身携带全部所需信息 |
| **JSON 可序列化** | 事件字段类型必须被 `System.Text.Json` 支持 |
| **Raise 是 protected** | 仅聚合根内部可触发，外部需通过聚合的业务方法间接调用 |

> ✅ **多实例部署**：框架已内置 4 种 lock provider（`None` / `RowLock` / `Distributed` / `Leader`），按部署形态选型即可。详见 10.5.1 节与 `src/10E0.Core/Events/Outbox/CLAUDE.md` 的"多实例安全"小节。
