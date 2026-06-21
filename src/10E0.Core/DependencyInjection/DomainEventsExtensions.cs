using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TenE0.Core.Events;
using TenE0.Core.Events.Outbox;
using TenE0.Core.Hosting;

namespace TenE0.Core.DependencyInjection;

public static class DomainEventsExtensions
{
    /// <summary>
    /// 注册领域事件 + Outbox 基础设施。
    ///
    /// TContext 仅需是 DbContext —— OutboxMessage 表由 TenE0SystemDbContext 自动注册。
    ///
    /// 一次性注册以下组件（调用方无需额外步骤）：
    /// - <see cref="OutboxInterceptor"/>：业务 SaveChanges 拦截 → 落 OutboxMessage
    /// - <see cref="IDomainEventDispatcher"/> + <see cref="IOutboxPublisher"/>：默认进程内
    /// - <see cref="OutboxRelayService{TContext}"/>：后台轮询投递
    /// - <see cref="IOutboxAdmin"/>：毒消息查询 / 导出 / 手动重试
    /// - <see cref="IOutboxLock"/>：行级锁契约（默认 <see cref="NoOpOutboxLock"/>，多实例部署时 Replace 为 provider 实现）
    /// - <see cref="OutboxSchemaSeeder"/>：表结构升级 seeder（#80：补齐 LockedUntil / LockedByInstance 列与索引）
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

        // 行级锁契约（#80 / #81）：0/1 实例部署零感知；多实例部署按 IOptions<OutboxRelayOptions>.LockProvider
        // + IDbContextFactory<TContext> 的 ProviderName 探测自动注册 SqlServer / Postgres provider 实现。
        // 沿用 OutboxAdminService<TContext> 已有泛型签名惯例 — 调用方一次 AddTenE0DomainEvents<TContext>()
        // 即可获得完整 Outbox 基础设施（后台投递 + 运维管理 + 行级锁）。
        services.AddOutboxLocking<TContext>();

        // Schema 升级 seeder（#80）：为既有库幂等补齐 LockedUntil / LockedByInstance 列与复合索引。
        // Order=0：先于任何业务 Seeder（业务 Seeder 通常 Order=10+），保证后续 seeder 写出的行能落在新列上。
        // TryAddEnumerable 允许业务方在特殊场景下 Replace 为自己的 seeder。
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDataSeeder, OutboxSchemaSeeder>());

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
