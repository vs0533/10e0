using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenE0.Core.DynamicFilters;

namespace TenE0.Core.DependencyInjection;

public static class DynamicFiltersExtensions
{
    /// <summary>
    /// 注册动态数据过滤系统。
    /// - IDynamicFilterProvider (Singleton): 缓存规则，在 OnModelCreating 时应用
    /// - IDataFilterRuleService (Scoped): 规则 CRUD 管理
    /// </summary>
    public static IServiceCollection AddTenE0DynamicFilters<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        // DynamicFilterProvider 是 Singleton（规则加载一次，缓存在内存）
        services.TryAddSingleton<IDynamicFilterProvider, DynamicFilterProvider>();

        // 管理服务是 Scoped（每请求独立事务）
        services.TryAddScoped<IDataFilterRuleService, DataFilterRuleService<TContext>>();

        return services;
    }
}
