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

## 消息生命周期

```
Pending → (Relay 轮询拾取) → Published (SentTime 非空)
                                ↓ (失败)
                           AttemptCount++, LastError 记录
                                ↓ (超过 MaxAttempts)
                           成为 Poison Message（不再处理）
```

## 已知风险

1. **多实例部署**：两个实例同时运行 RelayService 可能竞争同一消息。需要行级锁（`WITH (UPDLOCK, READPAST)`）或分布式锁。当前未实现
2. **Poison Message**：超过 MaxAttempts 的消息永远留在表中，无死信队列导出机制
3. **重复投递**：Handler 必须实现幂等性

## 设计决策

- **对比旧 MediatR 扩展**：旧版用 `MediatorExtension` 在 SaveChanges 后直接分发，有丢失事件的风险。新版同事务落库，零丢失
- `OccurredOn` 时间戳用于排序和调试，不参与业务逻辑
