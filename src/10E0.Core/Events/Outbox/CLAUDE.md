# Events/Outbox/ — Outbox Pattern 实现

领域事件的同事务持久化与异步发布。

## 文件说明

| 文件 | 职责 |
|------|------|
| `OutboxInterceptor.cs` | EF Core `SaveChangesInterceptor`：在 SaveChanges 前读取所有 AggregateRoot 的 PendingEvents，序列化为 `OutboxMessage` 行插入。与业务数据**同一事务**，保证原子性 |
| `OutboxMessage.cs` | 消息实体：`EventType`（AssemblyQualifiedName）、`Payload`（JSON）、`OccurredOn`、`SentTime`、`AttemptCount`、`LastError` |
| `IOutboxPublisher.cs` | 发布者接口 |
| `InProcessOutboxPublisher.cs` | 进程内发布：反序列化消息 → 路由到 `IDomainEventDispatcher` |
| `OutboxRelayService.cs` | 后台 `BackgroundService`：定期轮询未发送消息，批量发布。支持 `MaxAttempts` 重试 |
| `OutboxModelBuilderExtensions.cs` | EF Core 表映射 |
| `IOutboxAdmin.cs` | 死信（Poison Message）管理契约：查询 / 重试 / 导出三个操作；阈值复用 `OutboxRelayOptions.MaxAttempts`，禁止硬编码 |
| `OutboxAdminService.cs` | `IOutboxAdmin` 的泛型实现 `OutboxAdminService<TContext>`，从根容器的 `IDbContextFactory<TContext>` 解析 DbContext，避免重复声明泛型约束 |
| `OutboxPoisonMessageDto.cs` | 死信导出 DTO（不可变 record）：仅含排障关键字段（Id / EventType / Payload / OccurredOn / AttemptCount / LastError），与持久化层解耦 |

## 消息生命周期

```
Pending → (Relay 轮询拾取) → Published (SentTime 非空)
                                ↓ (失败)
                           AttemptCount++, LastError 记录
                                ↓ (超过 MaxAttempts)
                           成为 Poison Message（不再处理）
                                ↑ (IOutboxAdmin.RetryPoisonMessageAsync 重置)
                           AttemptCount=0, LastError=null → 下轮 Relay 重新拾取
```

## 已知风险

1. **多实例部署**：两个实例同时运行 RelayService 可能竞争同一消息。需要行级锁（`WITH (UPDLOCK, READPAST)`）或分布式锁。当前未实现
2. **Poison Message**：超过 MaxAttempts 的消息永远留在表中，无死信队列导出机制。**现已通过 `IOutboxAdmin` 提供查询/重试/导出入口**（参见 `OutboxAdminService<TContext>` 与 `OutboxPoisonMessageDto`），运维层可注册 Admin 端点或脚本消费这套契约进行死信排查与手动重试
3. **重复投递**：Handler 必须实现幂等性

## 设计决策

- **对比旧 MediatR 扩展**：旧版用 `MediatorExtension` 在 SaveChanges 后直接分发，有丢失事件的风险。新版同事务落库，零丢失
- `OccurredOn` 时间戳用于排序和调试，不参与业务逻辑
- **Admin 与 Relay 的查询条件对偶**：Relay 取 `SentTime IS NULL AND AttemptCount < MaxAttempts`；Admin 取 `SentTime IS NULL AND AttemptCount >= MaxAttempts`。两者共用 `OutboxRelayOptions.MaxAttempts`，阈值不可各自硬编码
- **Admin 重置不动 `SentTime`**：重试只清零 `AttemptCount` 与 `LastError`，保持 `SentTime == null`，下轮 Relay 自然拾取——避免引入新的状态字段