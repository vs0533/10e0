using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TenE0.Core.Events;
using TenE0.Core.Events.Outbox;

namespace TenE0.Core.DependencyInjection;

public static class DomainEventsExtensions
{
    /// <summary>
    /// 注册领域事件 + Outbox 基础设施。
    ///
    /// TContext 仅需是 DbContext —— OutboxMessage 表由 TenE0SystemDbContext 自动注册。
    ///
    /// 调用方还需：
    /// - <see cref="AddTenE0DomainEventHandlersFromAssembly"/> 扫描注册事件订阅者
    /// </summary>
    public static IServiceCollection AddTenE0DomainEvents<TContext>(
        this IServiceCollection services,
        Action<OutboxRelayOptions>? configureRelay = null)
        where TContext : DbContext
    {
        if (configureRelay is not null)
            services.Configure(configureRelay);
        else
            services.AddOptions<OutboxRelayOptions>();

        services.AddScoped<OutboxInterceptor>();
        services.TryAddSingleton<IDomainEventDispatcher, InProcessDomainEventDispatcher>();

        // 默认进程内投递。切换 Kafka/CAP/RabbitMQ 时只需：
        //   services.Replace(ServiceDescriptor.Scoped<IOutboxPublisher, KafkaOutboxPublisher>());
        // 业务代码（聚合、Handler、命令）无需任何改动。
        services.TryAddScoped<IOutboxPublisher, InProcessOutboxPublisher>();

        services.AddHostedService<OutboxRelayService<TContext>>();

        // 毒消息管理服务：查询 / 导出 / 手动重试。
        // 与 Relay 共用同一 TContext 泛型约束，与 RelayService<TContext> 在同一 AddTenE0DomainEvents<TContext> 调用点上对齐 —
        // 调用方一次 AddTenE0DomainEvents<TContext>() 即可获得完整 Outbox 基础设施（后台投递 + 运维管理）。
        // 显式工厂：OutboxAdminService<TContext> 构造函数签名依赖 IServiceProvider + IOptions<OutboxRelayOptions>，
        // 不能用 AddSingleton(typeof(OutboxAdminService<TContext>)) 这种 open-generic 简写（无法满足 ctor 参数名解析），
        // 故用工厂 sp => new OutboxAdminService<TContext>(sp, sp.GetRequiredService<IOptions<OutboxRelayOptions>>())。
        services.AddSingleton<IOutboxAdmin>(sp =>
            new OutboxAdminService<TContext>(sp, sp.GetRequiredService<IOptions<OutboxRelayOptions>>()));

        return services;
    }

    /// <summary>
    /// 扫描指定程序集并注册所有 IDomainEventHandler&lt;T&gt; 实现。
    /// </summary>
    public static IServiceCollection AddTenE0DomainEventHandlersFromAssembly(
        this IServiceCollection services,
        Assembly assembly)
    {
        var handlerInterface = typeof(IDomainEventHandler<>);

        foreach (var type in assembly.GetTypes().Where(t => !t.IsAbstract && !t.IsInterface))
        {
            foreach (var iface in type.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == handlerInterface))
            {
                services.AddScoped(iface, type);
            }
        }

        return services;
    }
}
