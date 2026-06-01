using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TenE0.Core.Events;

/// <summary>
/// IDomainEventDispatcher 进程内实现。
///
/// 实现策略（参考 CommandDispatcher 的 wrapper 模式）：
/// - 缓存"事件类型 → 调用器"映射，首次反射后无开销
/// - 单事件多 Handler：失败时记录每个 Handler 的异常，但不中断其他 Handler（fan-out 语义）
/// - 通过 IServiceScopeFactory 自建作用域，让 Singleton 的 Relay 也能调
/// </summary>
internal sealed class InProcessDomainEventDispatcher(
    IServiceScopeFactory scopeFactory,
    ILogger<InProcessDomainEventDispatcher> logger) : IDomainEventDispatcher
{
    private static readonly ConcurrentDictionary<Type, EventDispatchInvoker> InvokerCache = new();

    public async Task DispatchAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var invoker = InvokerCache.GetOrAdd(domainEvent.GetType(), static t =>
        {
            var wrapperType = typeof(EventDispatchInvokerImpl<>).MakeGenericType(t);
            return (EventDispatchInvoker)Activator.CreateInstance(wrapperType)!;
        });

        await using var scope = scopeFactory.CreateAsyncScope();
        await invoker.InvokeAsync(domainEvent, scope.ServiceProvider, logger, cancellationToken);
    }

    private abstract class EventDispatchInvoker
    {
        public abstract Task InvokeAsync(IDomainEvent evt, IServiceProvider sp, ILogger logger, CancellationToken ct);
    }

    private sealed class EventDispatchInvokerImpl<TEvent> : EventDispatchInvoker where TEvent : IDomainEvent
    {
        public override async Task InvokeAsync(IDomainEvent evt, IServiceProvider sp, ILogger logger, CancellationToken ct)
        {
            var typedEvent = (TEvent)evt;
            var handlers = sp.GetServices<IDomainEventHandler<TEvent>>().ToList();

            if (handlers.Count == 0)
            {
                logger.LogDebug("No handler subscribed for {EventType}", typeof(TEvent).Name);
                return;
            }

            // fan-out：一个 Handler 失败不应阻断其他 Handler
            List<Exception>? failures = null;
            foreach (var handler in handlers)
            {
                try
                {
                    await handler.HandleAsync(typedEvent, ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Domain event handler {Handler} failed for {EventType}",
                        handler.GetType().Name, typeof(TEvent).Name);
                    (failures ??= []).Add(ex);
                }
            }

            if (failures is { Count: > 0 })
                throw new AggregateException($"{failures.Count} 个 {typeof(TEvent).Name} 事件处理器失败", failures);
        }
    }
}
