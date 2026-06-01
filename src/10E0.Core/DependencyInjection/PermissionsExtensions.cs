using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenE0.Core.Abstractions;
using TenE0.Core.Permissions;
using TenE0.Core.Permissions.Behaviors;
using TenE0.Core.Permissions.DataFilter;
using TenE0.Core.Permissions.Management;
using TenE0.Core.Permissions.Storage;

namespace TenE0.Core.DependencyInjection;

public static class PermissionsExtensions
{
    /// <summary>
    /// 注册权限模块核心：评估器 + 管道行为 + 缓存 + 目录。
    /// 调用方需额外：
    /// - 注册 IPermissionStore 实现（用 <see cref="AddTenE0PermissionStorage{TContext}"/> 接 EF）
    /// - 注册各 IPermissionProvider（用 <see cref="AddTenE0PermissionsFromAssembly"/>）
    /// - 注册各 IEntityFilterContributor（数据行过滤规则）
    /// </summary>
    public static IServiceCollection AddTenE0Permissions(
        this IServiceCollection services,
        Action<PermissionsOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<PermissionsOptions>();

        services.TryAddScoped<IPermissionEvaluator, PermissionEvaluator>();
        services.TryAddScoped<IPermissionCache, DistributedPermissionCache>();
        services.TryAddSingleton<PermissionCatalog>();

        // 替换 Core 默认的 DataAccessPolicy → 基于超管角色的实现
        services.Replace(ServiceDescriptor.Scoped<IDataAccessPolicy, SuperUserDataAccessPolicy>());

        // 管道行为：开放泛型注册，所有命令自动接入
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(PermissionBehavior<,>));

        return services;
    }

    /// <summary>
    /// 启用基于 EF 的权限存储 + 管理服务。
    /// TContext 仅需是 DbContext —— 框架表 TenE0Role/TenE0RolePermission 由 TenE0SystemDbContext 自动注册。
    /// </summary>
    public static IServiceCollection AddTenE0PermissionStorage<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.Replace(ServiceDescriptor.Scoped<IPermissionStore, EfPermissionStore<TContext>>());
        services.TryAddScoped<IPermissionGrantService, PermissionGrantService<TContext>>();
        return services;
    }

    /// <summary>
    /// 扫描指定程序集并注册所有 IPermissionProvider / IEntityFilterContributor。
    /// </summary>
    public static IServiceCollection AddTenE0PermissionsFromAssembly(
        this IServiceCollection services,
        Assembly assembly)
    {
        foreach (var type in assembly.GetTypes().Where(t => !t.IsAbstract && !t.IsInterface))
        {
            if (typeof(IPermissionProvider).IsAssignableFrom(type))
                services.AddSingleton(typeof(IPermissionProvider), type);  // 静态定义，Singleton

            if (typeof(IEntityFilterContributor).IsAssignableFrom(type))
                services.AddScoped(typeof(IEntityFilterContributor), type);
        }

        return services;
    }
}
