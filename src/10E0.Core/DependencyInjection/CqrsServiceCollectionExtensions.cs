using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.Abstractions;
using TenE0.Core.Cqrs;
using TenE0.Core.Cqrs.Behaviors;

namespace TenE0.Core.DependencyInjection;

/// <summary>
/// CQRS 模块的 DI 注册扩展。
/// </summary>
public static class CqrsServiceCollectionExtensions
{
    /// <summary>
    /// 注册 CQRS 分发器 + 内置管道行为 + 扫描程序集注册所有 ICommandHandler 实现。
    ///
    /// 用法：
    ///     builder.Services.AddTenE0Cqrs(typeof(Program).Assembly);
    ///
    /// 与旧 services.AddMediatR(AssemblyUtils.AllAssembly.ToArray()) 的差异：
    /// - 调用方显式列出要扫描的程序集，避免全程序集扫描的不可预期性
    /// - 不再注册 IPipelineBehavior 的硬编码 (typeof(IPipelineBehavior&lt;,&gt;), typeof(LoggingBehavior&lt;,&gt;))
    ///   而是用统一开放泛型注册，外加可扩展点
    /// </summary>
    public static IServiceCollection AddTenE0Cqrs(this IServiceCollection services, params Assembly[] handlerAssemblies)
    {
        services.AddScoped<ICommandDispatcher, CommandDispatcher>();

        // 内置行为 — 开放泛型注册，所有命令类型自动套用
        // #41: Order 在 IPipelineBehavior 默认接口方法 + concrete override 上声明
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));

        // BehaviorOptions — #41: 配置 SkipBehaviorInTestEnv / DisabledInTest
        services.AddOptions<BehaviorOptions>();

        foreach (var assembly in handlerAssemblies.Distinct())
        {
            RegisterHandlersFromAssembly(services, assembly);
        }

        return services;
    }

    /// <summary>
    /// 为指定的 DbContext 类型注册事务行为。
    /// 只有实现 <see cref="ITransactional"/> 的命令会被此行为包裹。
    /// </summary>
    public static IServiceCollection AddTenE0TransactionBehavior<TContext>(this IServiceCollection services)
        where TContext : Microsoft.EntityFrameworkCore.DbContext
    {
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,,>).MakeGenericType(
            typeof(TransactionBehavior<,,>).GetGenericArguments()[0],
            typeof(TransactionBehavior<,,>).GetGenericArguments()[1],
            typeof(TContext)));

        return services;
    }

    /// <summary>
    /// #41 增量：注册一个 <see cref="IPipelineBehavior{TCommand, TResult}"/> 开放泛型实现。
    ///
    /// 用法：
    /// <code>
    /// services.AddBehavior&lt;MyCustomBehavior&lt;,&gt;&gt;();
    /// services.AddBehavior&lt;AnotherBehavior&lt;,,&gt;&gt;();
    /// </code>
    ///
    /// Order 由 <see cref="IPipelineBehavior{TCommand, TResult}.Order"/> 属性决定。
    /// 建议的 Order 值参考 <see cref="BuiltInBehaviorOrders"/>（框架保留 0~1000 给用户）。
    /// </summary>
    public static IServiceCollection AddBehavior<TBehavior>(this IServiceCollection services)
        where TBehavior : class
    {
        return services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TBehavior));
    }

    private static void RegisterHandlersFromAssembly(IServiceCollection services, Assembly assembly)
    {
        var handlerInterface = typeof(ICommandHandler<,>);

        var registrations =
            from type in assembly.GetTypes()
            where !type.IsAbstract && !type.IsInterface
            from iface in type.GetInterfaces()
            where iface.IsGenericType && iface.GetGenericTypeDefinition() == handlerInterface
            select (Service: iface, Implementation: type);

        foreach (var (service, implementation) in registrations)
        {
            services.AddScoped(service, implementation);
        }
    }
}
