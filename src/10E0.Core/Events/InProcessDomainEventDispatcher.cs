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
        // #109: 缓存 handler 解析器，避免每次 dispatch 都走 GetServices 的开放泛型解析。
        // handler 是 Scoped 生命周期，每次 dispatch 创建新 scope，实例不可缓存；
        // 但"如何从 sp 解析 handler"（解析器 delegate）按事件类型稳定，可缓存。
        // delegate 闭包捕获的 sp 由调用方传入，每次用新 scope 的 sp 解析出 scoped 实例。
        private static readonly Func<IServiceProvider, IEnumerable<IDomainEventHandler<TEvent>>> HandlerResolver =
            static sp => sp.GetServices<IDomainEventHandler<TEvent>>();

        public override async Task InvokeAsync(IDomainEvent evt, IServiceProvider sp, ILogger logger, CancellationToken ct)
        {
            var typedEvent = (TEvent)evt;

            // #109: 不再 .ToList() 物化 —— 直接遍历 IEnumerable，避免每次分配 List。
            // 首先枚举一次判断是否为空（避免无 handler 时进入 fan-out 循环的空开销）。
            List<Exception>? failures = null;
            var hasHandler = false;

            foreach (var handler in HandlerResolver(sp))
            {
                hasHandler = true;
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

            if (!hasHandler)
            {
                logger.LogDebug("No handler subscribed for {EventType}", typeof(TEvent).Name);
                return;
            }

            if (failures is { Count: > 0 })
                throw new AggregateException($"{failures.Count} 个 {typeof(TEvent).Name} 事件处理器失败", failures);
        }
    }
}
