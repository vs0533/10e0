using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenE0.Core.Abstractions;
using TenE0.Core.Auth;
using TenE0.Core.DataContext.Interceptors;
using TenE0.Core.Errors;
using TenE0.Core.Hosting;

namespace TenE0.Core.DependencyInjection;

/// <summary>
/// 10E0 核心服务注册扩展。
///
/// 替代旧 E0ServicesExtensions.AddE0Context，更显式、更细粒度：
/// - 不再全局 ServiceLocator
/// - 不再注册 EmptyDataContext 占位符
/// - 拦截器、TimeProvider 等都用接口注入
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 10E0 核心服务（不含 DbContext）。
    /// DbContext 由调用方通过 <see cref="AddTenE0DataContext{TContext}"/> 单独注册。
    /// </summary>
    public static IServiceCollection AddTenE0Core(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();

        // TimeProvider：测试可换 FakeTimeProvider
        services.TryAddSingleton(TimeProvider.System);

        // 默认内存分布式缓存。生产环境替换为 Redis：
        //   services.AddStackExchangeRedisCache(...)
        services.AddDistributedMemoryCache();

        // 当前用户上下文（无状态，从 ClaimsPrincipal 读）
        services.TryAddScoped<ICurrentUserContext, HttpCurrentUserContext>();

        // 错误袋（Scoped，每请求独立）
        services.TryAddScoped<IErrs, Errs>();

        // 数据访问策略默认实现 — 从不 bypass；Permissions 模块可覆盖
        services.TryAddScoped<IDataAccessPolicy, DefaultDataAccessPolicy>();

        // 审计拦截器
        services.AddScoped<AuditInterceptor>();

        return services;
    }

    /// <summary>
    /// 切换为非 HTTP 场景的用户上下文（AsyncLocal 实现）。
    /// 用于后台 Worker / 消息消费者 / 控制台 / 测试等无 HttpContext 的环境。
    ///
    /// 调用方需通过 <see cref="ICurrentUserContextSetter.Impersonate"/> 在作用域内设置当前用户。
    /// </summary>
    public static IServiceCollection UseTenE0AmbientCurrentUser(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<ICurrentUserContext, AmbientCurrentUserContext>());
        services.AddSingleton<ICurrentUserContextSetter>(sp =>
            (AmbientCurrentUserContext)sp.GetRequiredService<ICurrentUserContext>());
        return services;
    }

    /// <summary>
    /// 注册 DbContext + 拦截器 + 数据库初始化服务。
    ///
    /// 用 IDbContextFactory 而非传统 AddDbContext：
    /// - 旧反射工厂 (Connection.Create + EmptyDataContext) 彻底消除
    /// - 工厂模式天然支持后台任务、并行操作
    /// - 多数据源场景可结合 Keyed Services
    /// </summary>
    public static IServiceCollection AddTenE0DataContext<TContext>(
        this IServiceCollection services,
        Action<IServiceProvider, DbContextOptionsBuilder> optionsAction)
        where TContext : DbContext
    {
        // Scoped 生命周期：每个请求作用域拥有独立 Factory 实例，
        // 这样可以注入 Scoped 拦截器（依赖 ICurrentUserContext → HttpContext）。
        // .NET 10 推荐做法 — 工厂模式仍然有效，CreateDbContext() 行为不变。
        services.AddDbContextFactory<TContext>((sp, options) =>
        {
            optionsAction(sp, options);
            options.AddInterceptors(sp.GetRequiredService<AuditInterceptor>());

            // OutboxInterceptor 是可选的（仅当用户调用 AddTenE0DomainEvents 才注册）
            // 用 GetService 避免依赖缺失时崩溃
            var outbox = sp.GetService<TenE0.Core.Events.Outbox.OutboxInterceptor>();
            if (outbox is not null) options.AddInterceptors(outbox);
        }, lifetime: ServiceLifetime.Scoped);

        // 启动时初始化（IHostedLifecycleService.StartingAsync 在端口监听前完成）
        services.AddHostedService<DatabaseInitializerService<TContext>>();

        return services;
    }
}
