namespace TenE0.Core.Events;

/// <summary>
/// 领域事件分发器。
///
/// 默认实现：进程内反射查找 IDomainEventHandler&lt;T&gt; 并依次调用。
///
/// 替换为分布式实现时：实现一个把事件序列化后投递到 Kafka/RabbitMQ 的版本，
/// 下游服务在自己进程内有本类的 in-process 实现接收。
/// </summary>
public interface IDomainEventDispatcher
{
    /// <summary>分发单个事件到所有订阅者。</summary>
    Task DispatchAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default);
}
