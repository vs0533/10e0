using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenE0.Core.Configuration;

namespace TenE0.Core.DependencyInjection;

/// <summary>
/// Configuration 模块（数据字典 + 系统参数）DI 注册。
/// 镜像 <c>MenusExtensions</c> / <c>PermissionsExtensions</c> 范式。
/// </summary>
public static class ConfigurationExtensions
{
    /// <summary>
    /// 注册数据字典 + 系统参数服务。
    /// 实体由 <c>TenE0SystemDbContext</c> 自动接入表配置，此处仅注册服务。
    /// </summary>
    /// <param name="configure">可选 <see cref="ConfigurationOptions"/> 配置。</param>
    public static IServiceCollection AddTenE0Configuration<TContext>(
        this IServiceCollection services,
        Action<ConfigurationOptions>? configure = null)
        where TContext : DbContext
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<ConfigurationOptions>();

        services.AddTenE0Caching();

        services.TryAddScoped<IDataDictionaryService, DataDictionaryService<TContext>>();
        services.TryAddScoped<ISystemParameterStore, SystemParameterStore<TContext>>();
        // 注册表收集业务方实现的 ISystemParameterDefinition（业务侧未实现时为空 —— 退化为只读）
        services.TryAddSingleton<SystemParameterRegistry>();

        return services;
    }
}
