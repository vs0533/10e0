# Events/ — 领域事件

DDD 领域事件的发布与处理基础设施。

## 文件说明

| 文件 | 职责 |
|------|------|
| `IDomainEvent.cs` | 领域事件标记接口（不可变 record） |
| `IDomainEventHandler.cs` | 事件处理器接口 `IDomainEventHandler<in TEvent>` |
| `IDomainEventDispatcher.cs` | 事件分发器接口 |
| `AggregateRoot.cs` | 聚合根基类：`_pendingEvents` 列表、`Raise()` 注册事件、`ClearEvents()` 供 OutboxInterceptor 消费 |
| `InProcessDomainEventDispatcher.cs` | 进程内分发：fan-out 到所有 handler，单个 handler 失败不阻塞其他。静态缓存 invoker（零热路径反射） |

## 聚合根用法

```csharp
public class Order : AggregateRoot
{
    public void Place()
    {
        // 业务逻辑...
        Raise(new OrderPlacedEvent(Id, TotalAmount));
    }
}
```

## 事件流

```
业务代码 Raise(event)
    → AggregateRoot._pendingEvents 列表
    → OutboxInterceptor.SavingChangesAsync 拦截
    → 序列化为 OutboxMessage 行（同事务）
    → SaveChanges 提交
    → OutboxRelayService 轮询发布
```

## 与继承链的关系

`AggregateRoot` 继承 `AuditedEntity`（软删除 + 审计字段），可直接用于业务实体。

## 子目录

| 目录 | 职责 |
|------|------|
| `Outbox/` | Outbox Pattern 实现（同事务落库 + 后台 Relay） |
