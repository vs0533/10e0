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

        // #11 multi-tenancy: 租户上下文（无状态，从 JWT tenant_id claim 读）。
        // 与 ICurrentUserContext 共享同一 IHttpContextAccessor 实例。
        services.TryAddScoped<ITenantContext, HttpTenantContext>();

        // 错误袋（Scoped，每请求独立）
        services.TryAddScoped<IErrs, Errs>();

        // 数据访问策略默认实现 — 从不 bypass；Permissions 模块可覆盖
        services.TryAddScoped<IDataAccessPolicy, DefaultDataAccessPolicy>();

        // #43 下沉：默认 IUserInfoLoader 空实现。框架不依赖具体 IdP；
        // 业务模块在 IAppModule.ConfigureServices 中 services.Replace(...) 覆盖。
        services.TryAddScoped<IUserInfoLoader, NullUserInfoLoader>();

        // 审计拦截器（Singleton）：
        // .NET 10 中 AddDbContextFactory 默认注册 IDbContextFactory 为 Singleton，
        // factory 创建 DbContextOptions 时会把拦截器实例嵌进去。如果 AuditInterceptor 是
        // Scoped，它在 root scope 创建后被所有请求 scope 共享 → captive dependency。
        // 改为 Singleton 后，AuditInterceptor 通过 IHttpContextAccessor（也是 Singleton）
        // 在 SavingChanges 时按需解析当前请求 scope 的 ICurrentUserContext，避免共享状态。
        services.AddSingleton<AuditInterceptor>();

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
        // .NET 10 中 AddDbContextFactory 的 lifetime 默认是 Singleton（lifetime 参数同时
        // 控制 factory 和 options 的生命周期）。传 Scoped 会被 .NET 10 DI scope 验证拦截
        // （captive dependency：Singleton factory 不能消费 Scoped DbContextOptions）。
        // 拦截器走 Singleton：AuditInterceptor 通过 IHttpContextAccessor 按需解析当前用户，
        // OutboxInterceptor 只依赖 Singleton TimeProvider。
        services.AddDbContextFactory<TContext>((sp, options) =>
        {
            optionsAction(sp, options);
            options.AddInterceptors(sp.GetRequiredService<AuditInterceptor>());

            // OutboxInterceptor 是可选的（仅当用户调用 AddTenE0DomainEvents 才注册）
            // 用 GetService 避免依赖缺失时崩溃
            var outbox = sp.GetService<TenE0.Core.Events.Outbox.OutboxInterceptor>();
            if (outbox is not null) options.AddInterceptors(outbox);
        });

        // 启动时初始化（IHostedLifecycleService.StartingAsync 在端口监听前完成）
        services.AddHostedService<DatabaseInitializerService<TContext>>();

        return services;
    }
}
