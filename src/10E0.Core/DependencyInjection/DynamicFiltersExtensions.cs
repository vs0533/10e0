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
    /// - ITableNameProvider (Singleton): 实体 → 物理表名解析器（issue #40）
    /// - 4 个内置 IDbProviderFactoryDescriptor (Singleton): SQL Server / PostgreSQL / MySQL / SQLite
    ///   业务项目可注入自定义 descriptor 接入国产 DB
    /// </summary>
    public static IServiceCollection AddTenE0DynamicFilters<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        // DynamicFilterProvider 是 Singleton（规则加载一次，缓存在内存）
        services.TryAddSingleton<IDynamicFilterProvider, DynamicFilterProvider>();

        // 管理服务是 Scoped（每请求独立事务）
        services.TryAddScoped<IDataFilterRuleService, DataFilterRuleService<TContext>>();

        // 表名抽象（#40）：默认实现读取 TableNameOptions，业务可在 ConfigureTenE0TableNames 中覆盖
        services.TryAddSingleton<ITableNameProvider, DefaultTableNameProvider>();

        // DbProviderFactory descriptors（#40）：内置 4 个，业务可再 AddSingleton<DmDbProviderFactoryDescriptor>() 追加
        // 业务注入同名实现可 Replace 覆盖框架默认
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDbProviderFactoryDescriptor, SqlServerDbProviderFactoryDescriptor>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDbProviderFactoryDescriptor, NpgsqlDbProviderFactoryDescriptor>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDbProviderFactoryDescriptor, MySqlDbProviderFactoryDescriptor>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDbProviderFactoryDescriptor, SqliteDbProviderFactoryDescriptor>());

        return services;
    }

    /// <summary>
    /// 配置 <see cref="TableNameOptions"/>：可选 <c>Prefix</c>（如 <c>"MyApp_"</c>）与
    /// <c>Schema</c>（如 <c>"crm"</c>）。Step 2/3 会在 <c>ConfigureConventions</c> 阶段接入
    /// <c>TableNameConvention</c>，使 EF Core 自动应用。
    /// </summary>
    public static IServiceCollection ConfigureTenE0TableNames(
        this IServiceCollection services,
        Action<TableNameOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        services.Configure(configure);
        return services;
    }
}
