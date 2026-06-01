namespace TenE0.Core.Events;

/// <summary>
/// 领域事件处理器。
///
/// 用法：
///     public sealed class SendApprovalNotificationHandler
///         : IDomainEventHandler&lt;ApplicationApprovedEvent&gt;
///     {
///         public async Task HandleAsync(ApplicationApprovedEvent evt, CancellationToken ct)
///         {
///             // 发邮件 / 发短信 / 推 IM
///         }
///     }
///
/// 一个事件可以有多个处理器（fan-out），它们独立运行：
/// - 单体阶段：In-process Dispatcher 依次调用
/// - 分布式阶段：每个 Handler 部署到不同服务，订阅 Kafka topic
///
/// Handler 必须幂等！同一事件可能因为重试被投递多次。
/// </summary>
public interface IDomainEventHandler<in TEvent> where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken);
}
