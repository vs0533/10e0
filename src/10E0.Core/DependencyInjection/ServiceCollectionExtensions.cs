using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenE0.Core.Abstractions;
using TenE0.Core.Auditing;
using TenE0.Core.Auth;
using TenE0.Core.Auth.Jwt.Storage;
using TenE0.Core.DataContext.Interceptors;
using TenE0.Core.Errors;
using TenE0.Core.Hosting;
using TenE0.Core.Workflow.DependencyInjection;

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
    /// DbContext 由调用方通过 <c>AddTenE0DataContext&lt;TContext&gt;(...)</c> 单独注册。
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

        // #152 审计日志 Sink 默认空实现 —— 保证未调用 AddTenE0Auditing 时，
        // auth command handlers 仍能解析到 IAuditLogSink（变 No-op）。
        // 调用 AddTenE0Auditing 后用 services.Replace(...) 覆盖为真实实现。
        services.TryAddScoped<TenE0.Core.Auditing.IAuditLogSink, TenE0.Core.Auditing.NullAuditLogSink>();

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

            // AuditLogInterceptor 是可选的（仅当用户调用 AddTenE0Auditing 才注册）
            // 用 GetService 避免依赖缺失时崩溃 —— 对齐 OutboxInterceptor 的可选注入模式
            var auditLog = sp.GetService<AuditLogInterceptor>();
            if (auditLog is not null) options.AddInterceptors(auditLog);
        });

        // 启动时初始化（IHostedLifecycleService.StartingAsync 在端口监听前完成）
        services.AddHostedService<DatabaseInitializerService<TContext>>();

        return services;
    }

    /// <summary>
    /// 用连接串注册 <see cref="IDbContextFactory{TContext}"/>（issue #160 简化重载 1）。
    ///
    /// <para>
    /// <b>provider 装配策略</b>：Core 不直接引用 SqlServer / Npgsql / Sqlite 包（避免框架膨胀，
    /// 与 Microsoft <c>AddDbContext</c> 设计一致）。provider 的 <c>UseSqlServer</c> /
    /// <c>UseNpgsql</c> / <c>UseSqlite</c> 扩展方法在 app 层调用 —— 调用方通过
    /// <see cref="IDbProviderConfigurator"/> SPI 注册（见 <see cref="AddTenE0DbProviderConfigurator"/>）。
    /// </para>
    ///
    /// <para>
    /// <paramref name="provider"/> 为 <c>null</c> 时由 <see cref="ConnectionStringProbe.Detect"/> 探测，
    /// 探测失败抛 <see cref="InvalidOperationException"/>（提示显式传 <paramref name="provider"/>）。
    /// </para>
    /// </summary>
    /// <param name="connectionString">连接串。</param>
    /// <param name="provider">显式指定 provider；<c>null</c> 自动探测。</param>
    public static IServiceCollection AddTenE0DataContext<TContext>(
        this IServiceCollection services,
        string connectionString,
        DatabaseProvider? provider = null)
        where TContext : DbContext
        => services.AddTenE0DataContext<TContext>(connectionString, provider, extraConfigure: null);

    /// <summary>
    /// 用连接串 + 额外 options 修饰注册 <see cref="IDbContextFactory{TContext}"/>（issue #160 简化重载 2）。
    ///
    /// <paramref name="extraConfigure"/> 在 provider 装配 <b>之后</b> 调用，用于追加
    /// <c>EnableSensitiveDataLogging</c> / 自定义 <c>MigrationsAssembly</c> 等修饰。
    /// </summary>
    public static IServiceCollection AddTenE0DataContext<TContext>(
        this IServiceCollection services,
        string connectionString,
        DatabaseProvider? provider,
        Action<DbContextOptionsBuilder>? extraConfigure)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        var resolved = provider ?? ConnectionStringProbe.Detect(connectionString);

        return services.AddTenE0DataContext<TContext>((sp, options) =>
        {
            if (!DbContextProviderRegistry.TryConfigure(sp, resolved, connectionString, options))
            {
                throw new InvalidOperationException(
                    $"未注册 {resolved} 的 IDbProviderConfigurator。请在 app 启动时调用 " +
                    $"services.AddTenE0DbProviderConfigurator(new NpgsqlConfigurator()) 等注册你的 provider 装配器 " +
                    $"（SqlServer/Npgsql/Sqlite 包应在 app 层引用）。");
            }
            extraConfigure?.Invoke(options);
        });
    }

    /// <summary>
    /// 从 <see cref="IConfiguration"/> 读连接串后注册 <see cref="IDbContextFactory{TContext}"/>
    /// （issue #160 简化重载 3）。等价于 <c>AddTenE0DataContext&lt;T&gt;(configuration.GetConnectionString(name), provider)</c>。
    /// </summary>
    /// <param name="configuration">配置根。</param>
    /// <param name="connectionStringName">连接串名（默认 <c>Default</c>）。</param>
    /// <param name="provider">显式指定 provider；<c>null</c> 自动探测。</param>
    public static IServiceCollection AddTenE0DataContext<TContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionStringName = "Default",
        DatabaseProvider? provider = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString(connectionStringName)
            ?? throw new InvalidOperationException(
                $"配置中未找到 ConnectionStrings:{connectionStringName}。");
        return services.AddTenE0DataContext<TContext>(connectionString, provider);
    }

    /// <summary>
    /// 注册一个 <see cref="IDbProviderConfigurator"/> —— 把 provider 的 <c>UseXxx</c> 装配
    /// 从框架侧（不引用 provider 包）下沉到 app 侧。app 启动时调用：
    /// <code>
    /// builder.Services.AddTenE0DbProviderConfigurator(new NpgsqlConfigurator());
    /// </code>
    /// </summary>
    public static IServiceCollection AddTenE0DbProviderConfigurator(
        this IServiceCollection services,
        IDbProviderConfigurator configurator)
    {
        ArgumentNullException.ThrowIfNull(configurator);
        services.Replace(ServiceDescriptor.Singleton(configurator));
        return services;
    }

    /// <summary>
    /// <see cref="AddTenE0All{TContext}"/> / <see cref="AddTenE0All{TUser,TContext}"/>
    /// 一键聚合注册所有官方模块（issue #160）。
    ///
    /// <para>
    /// 默认启用：Core / EntityService / DataContext / Cqrs / Permissions / Identity / Menus /
    /// Sequences / DomainEvents / DynamicFilters / Configuration。按需启用（默认关）：
    /// Files / Auditing / ImportExport / Realtime / Workflow —— 见 <see cref="TenE0Options"/> 各开关。
    /// </para>
    ///
    /// <para>
    /// <b>与 <c>IAppModule</c> 协同</b>：<c>AddTenE0All</c> 注册<b>框架</b>，业务模块走
    /// <see cref="AppModuleExtensions.AddAppModule{TModule}"/> 注册<b>业务</b>。两者并行，互不互斥。
    /// </para>
    ///
    /// <para>用户类型用默认 <see cref="TenE0User"/>；扩展用户字段请用
    /// <see cref="AddTenE0All{TUser,TContext}(IServiceCollection, IConfiguration, Action{TenE0Options}?)"/>。</para>
    /// </summary>
    /// <param name="configuration">配置根（读 ConnectionStrings / Jwt 等）。</param>
    /// <param name="configure">聚合选项；不传用默认值。</param>
    public static IServiceCollection AddTenE0All<TContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<TenE0Options>? configure = null)
        where TContext : DbContext
        => services.AddTenE0All<TenE0User, TContext>(configuration, configure);

    /// <summary>
    /// <see cref="AddTenE0All{TContext}"/> 的扩展用户类型重载 ——
    /// 业务方扩展 <see cref="TenE0User"/> 字段时使用 <c>AddTenE0All&lt;AppUser, AppDbContext&gt;(...)</c>。
    /// </summary>
    public static IServiceCollection AddTenE0All<TUser, TContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<TenE0Options>? configure = null)
        where TUser : TenE0User
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var opt = new TenE0Options();
        configure?.Invoke(opt);

        var handlerAssemblies = opt.HandlerAssemblies ?? [Assembly.GetEntryAssembly()!];
        var workflowAssemblies = opt.WorkflowAssemblies ?? handlerAssemblies;
        var connectionString = opt.ConnectionString
            ?? configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "TenE0Options.ConnectionString 未设置，且配置中未找到 ConnectionStrings:Default。");

        // 基础套件（始终启用）
        services
            .AddTenE0Core()
            .AddTenE0EntityService()
            .AddTenE0DataContext<TContext>(connectionString, opt.Provider)
            .AddTenE0Cqrs(handlerAssemblies);

        foreach (var asm in handlerAssemblies)
            services.AddTenE0PermissionsFromAssembly(asm);

        // Identity（JWT + Permissions + Organizations）。必填配置。
        if (opt.Identity is null)
        {
            throw new InvalidOperationException(
                "TenE0Options.Identity 必须配置（至少设 Jwt.SigningKey）。" +
                "用 builder.Services.AddTenE0All<TContext>(configuration, opt => { opt.Identity = jwt => {...}; });");
        }
        services.AddTenE0Identity<TUser, TContext>(opt.Identity);

        // 基础套件（默认 true）
        if (opt.Menus) services.AddTenE0Menus<TContext>();
        if (opt.Sequences) services.AddTenE0Sequences<TContext>();
        if (opt.DomainEvents)
        {
            services.AddTenE0DomainEvents<TContext>(opt.DomainEventsOptions);
            foreach (var asm in handlerAssemblies)
                services.AddTenE0DomainEventHandlersFromAssembly(asm);
        }
        if (opt.DynamicFilters) services.AddTenE0DynamicFilters<TContext>();
        if (opt.Configuration) services.AddTenE0Configuration<TContext>(opt.ConfigurationOptions);

        // 按需启用项（默认 false）
        if (opt.Files) services.AddTenE0Files<TContext>(opt.FilesOptions);
        if (opt.Auditing) services.AddTenE0Auditing<TContext>(opt.AuditingOptions);
        if (opt.ImportExport) services.AddTenE0ImportExport(opt.ImportExportOptions);
        if (opt.Realtime)
        {
            services.AddTenE0Realtime(opt.RealtimeOptions);
            services.AddRealtimeHubTokenFromQuery();
        }
        if (opt.Workflow)
        {
            foreach (var asm in workflowAssemblies)
                services.AddTenE0WorkflowStateMachine(asm);
            services.AddTenE0WorkflowDefinitions<TContext>();
            services.AddTenE0WorkflowRuntime<TContext>(opt.WorkflowOptions);
        }

        return services;
    }
}

/// <summary>
/// EF Core provider 装配器 SPI —— 让 app 层把 <c>UseSqlServer</c> / <c>UseNpgsql</c> /
/// <c>UseSqlite</c> 等 provider 扩展方法注入到框架的 <see cref="AddTenE0DataContext{TContext}"/>
/// 连接串重载中（issue #160）。
///
/// <para>
/// Core 不引用任何 provider 包，所以<b>不能</b>直接调 <c>UseSqlServer</c>。app 层实现本接口，
/// 通过 <see cref="ServiceCollectionExtensions.AddTenE0DbProviderConfigurator"/> 注册，
/// 框架在装配 DbContextOptions 时按探测 / 显式 provider 调用对应实现。
/// </para>
/// </summary>
public interface IDbProviderConfigurator
{
    /// <summary>本装配器支持的 provider。</summary>
    DatabaseProvider Provider { get; }

    /// <summary>把 provider 的 <c>UseXxx</c> 应用到 <paramref name="options"/>。</summary>
    void Configure(IServiceProvider services, DbContextOptionsBuilder options, string connectionString);
}

/// <summary>
/// <see cref="IDbProviderConfigurator"/> 注册表（按 provider 解析已注册装配器）。
/// 纯 SPI：Core 不内置任何 provider 装配器（不引用 SqlServer/Npgsql/Sqlite/InMemory 包），
/// 调用方通过 <see cref="AddTenE0DbProviderConfigurator"/> 注册自己引用的 provider。
/// </summary>
internal static class DbContextProviderRegistry
{
    public static bool TryConfigure(
        IServiceProvider services,
        DatabaseProvider provider,
        string connectionString,
        DbContextOptionsBuilder options)
    {
        var configurators = services.GetServices<IDbProviderConfigurator>();
        foreach (var c in configurators)
        {
            if (c.Provider == provider)
            {
                c.Configure(services, options, connectionString);
                return true;
            }
        }
        return false;
    }
}
