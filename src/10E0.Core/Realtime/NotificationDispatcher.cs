using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TenE0.Core.Events;

namespace TenE0.Core.Realtime;

/// <summary>
/// 声明式实时触发的桥接器（#155）。
///
/// 注册为 <b>开放泛型</b> <c>IDomainEventHandler&lt;TEvent&gt;</c>（约束 <c>TEvent : INotifyClient</c>），
/// 由 DI 按每个具体事件类型自动构建。任何实现 <see cref="INotifyClient"/> 的领域事件一经
/// <see cref="IDomainEventDispatcher"/> 派发，本类即把它的 <see cref="INotifyClient.Target"/> 交给
/// <see cref="IRealtimeNotifier"/> 投递 —— 业务方零样板。
///
/// 容错：推送是 best-effort，本处理器抛出的异常会被 <c>InProcessDomainEventDispatcher</c> 记录但不影响
/// 其他 handler（fan-out 语义）。这里再包一层 try/catch 是为了让"前端推送失败"绝不阻塞业务事务的其它事件处理器。
///
/// traceId：取 <see cref="Activity.Current"/>（与审计拦截器一致），缺省时附空。
/// </summary>
public sealed class NotificationDispatcher<TEvent>(
    IRealtimeNotifier notifier,
    ILogger<NotificationDispatcher<TEvent>> logger) : IDomainEventHandler<TEvent>
    where TEvent : INotifyClient
{
    public async Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken)
    {
        NotificationTarget target;
        try
        {
            target = domainEvent.Target;
        }
        catch (Exception ex)
        {
            // Target 计算抛异常（如事件字段不合法）不应阻塞其它 handler
            logger.LogError(ex, "读取 {Event} 的 NotificationTarget 失败，跳过实时推送", typeof(TEvent).Name);
            return;
        }

        var traceId = target.TraceId ?? Activity.Current?.TraceId.ToString();

        try
        {
            switch (target.Delivery)
            {
                case NotificationTarget.Scope.User:
                    await notifier.NotifyUserAsync(target.Recipient!, target.EventName, target.Payload, traceId, cancellationToken);
                    break;
                case NotificationTarget.Scope.Group:
                    await notifier.NotifyGroupAsync(target.Recipient!, target.EventName, target.Payload, traceId, cancellationToken);
                    break;
                default: // All
                    await notifier.NotifyAllAsync(target.EventName, target.Payload, traceId, cancellationToken);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "实时推送 {Event} 失败（{Delivery}/{Recipient}）",
                target.EventName, target.Delivery, target.Recipient);
        }
    }
}
