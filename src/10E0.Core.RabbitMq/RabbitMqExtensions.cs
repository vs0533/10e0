using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenE0.Core.DependencyInjection;
using TenE0.Core.Events.Outbox;

namespace TenE0.Core.Messaging.RabbitMq;

/// <summary>
/// RabbitMQ Publisher 的 DI 注册扩展（issue #165）。
///
/// <para>
/// 用法：先 <c>AddTenE0DomainEvents&lt;TContext&gt;()</c>（注册默认进程内 Publisher），
/// 再 <c>AddTenE0RabbitMqPublisher()</c> <b>Replace</b> 为 RabbitMQ 实现 —— 业务代码零改动。
/// </para>
/// <code>
/// builder.Services.AddTenE0DomainEvents&lt;AppDbContext&gt;();
/// builder.Services.AddTenE0RabbitMqPublisher(opt =&gt;
/// {
///     opt.Connection.HostName = "rabbitmq";
///     opt.Exchange.Name = "tene0.domain-events";
/// });
/// </code>
/// </summary>
public static class RabbitMqExtensions
{
    /// <summary>
    /// 把 <see cref="IOutboxPublisher"/> 替换为 <see cref="RabbitMqPublisher"/>，并注册连接管理器 + 健康检查。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="configure">选项回调；不传用默认值。</param>
    public static IServiceCollection AddTenE0RabbitMqPublisher(
        this IServiceCollection services,
        Action<RabbitMqOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<RabbitMqOptions>();

        RegisterPublisherCore(services);
        return services;
    }

    /// <summary>
    /// 重载：从 <see cref="IConfiguration"/> 的 <c>"RabbitMq"</c> 段绑定选项，再叠加可选回调。
    /// </summary>
    public static IServiceCollection AddTenE0RabbitMqPublisher(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<RabbitMqOptions>? configure = null)
    {
        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.SectionName));
        if (configure is not null)
            services.Configure(configure);

        RegisterPublisherCore(services);
        return services;
    }

    private static void RegisterPublisherCore(IServiceCollection services)
    {
        // Replace 而非 TryAdd —— 显式覆盖 AddTenE0DomainEvents 注册的默认进程内 Publisher。
        // Scoped 与 IOutboxPublisher 现有注册生命周期一致（Relay 每 scope 解析一个）。
        services.Replace(ServiceDescriptor.Scoped<IOutboxPublisher, RabbitMqPublisher>());

        // 连接管理器 Singleton：连接池跨请求复用，整个进程生命周期持有。
        // Publisher / HealthCheck 依赖 IRabbitMqConnectionManager 接口（便于单测 mock）。
        services.TryAddSingleton<IRabbitMqConnectionManager, RabbitMqConnectionManager>();

        // 健康检查接入 #161 ready 探针（与 ObservabilityExtensions.ReadyTag 对齐）。
        services.AddHealthChecks()
            .AddCheck<RabbitMqHealthCheck>(
                name: "rabbitmq",
                failureStatus: null,
                tags: [ObservabilityExtensions.ReadyTag]);
    }
}
