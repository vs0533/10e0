namespace TenE0.Core.Events.Outbox;

/// <summary>
/// Outbox 消息投递抽象。
///
/// 这是 OutboxRelayService 与"投递机制"之间的唯一接缝。
/// 切换不同的投递机制时，业务代码（聚合、Handler、命令）零改动，仅需替换此服务的 DI 注册：
///
///   默认（进程内）:
///     services.AddTenE0DomainEvents&lt;AppDbContext&gt;();   // 已注册 InProcessOutboxPublisher
///
///   切到 Kafka:
///     services.Replace(ServiceDescriptor.Scoped&lt;IOutboxPublisher, KafkaOutboxPublisher&gt;());
///
///   切到 CAP:
///     services.Replace(ServiceDescriptor.Scoped&lt;IOutboxPublisher, CapOutboxPublisher&gt;());
///
/// 实现注意：
/// - 收到的 OutboxMessage 含 EventType（CLR 全名）+ Payload（JSON 串）
/// - 进程内实现需要反序列化为 IDomainEvent 后调 IDomainEventDispatcher
/// - 远程实现（Kafka/RabbitMQ）通常无需反序列化，直接以 (topic, payload) 形式投递
/// - 抛出异常 = 投递失败，OutboxRelayService 会重试（AttemptCount 自增）
/// </summary>
public interface IOutboxPublisher
{
    /// <summary>发布一条 outbox 消息。失败抛异常即可，Relay 负责重试 + 错误记录。</summary>
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken);
}
