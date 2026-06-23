using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenE0.Core.Caching;

namespace TenE0.Core.DependencyInjection;

/// <summary>
/// 多级缓存 + 原子计数器 DI 注册扩展。
/// </summary>
public static class CachingExtensions
{
    /// <summary>
    /// 注册 <see cref="IMultiLevelCache"/> 默认实现（L1 IMemoryCache + L2 IDistributedCache）。
    ///
    /// 业务项目可调 <c>services.Replace(...)</c> 切换为 HybridCache / Redis / 自定义实现。
    /// 同样的，<see cref="IAtomicCounter"/> 默认走 IDistributedCache 上的
    /// <c>GetString → +1 → SetString</c>；生产 Redis 建议替换为原生 <c>INCR</c> 实现。
    ///
    /// 依赖：
    /// - <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/>：未注册时由本扩展注册默认 MemoryCache
    ///   （#101: 默认带 16 MB SizeLimit 兜底防 OOM，业务项目可通过 Func 重载或 IConfiguration 重载覆盖）。
    ///   **如果业务项目先于本方法自行注册了 <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/>，
    ///   <c>TryAddSingleton</c> 不会替换，SizeLimit 兜底会失效**——这是 .NET Options "用户优先"惯例的代价，
    ///   如需兜底请改用 <see cref="AddTenE0Caching(IServiceCollection, Func{CachingOptions, CachingOptions})"/>
    ///   或 <see cref="AddTenE0Caching(IServiceCollection, IConfiguration)"/> 重载并先注册自己的 MemoryCache。
    /// - <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/>：通常由 AddTenE0Core() 注入
    /// </summary>
    public static IServiceCollection AddTenE0Caching(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // L1：未显式注册时给个默认 MemoryCache。Singleton 是 MemoryCache 的标准生命周期。
        // #101: SizeLimit + CompactionPercentage 兜底 OOM —— 缺省会按 entry 数无限制堆积。
        services.TryAddSingleton<Microsoft.Extensions.Caching.Memory.IMemoryCache>(
            _ => new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions
                {
                    SizeLimit = CachingOptions.Default.SizeLimit,
                    CompactionPercentage = CachingOptions.Default.CompactionPercentage,
                }));

        // 默认实现 — 业务项目用 services.Replace(...) 覆盖
        services.TryAddSingleton<IMultiLevelCache, MultiLevelCache>();
        services.TryAddSingleton<IAtomicCounter, DistributedAtomicCounter>();
        return services;
    }

    /// <summary>
    /// 注册 <see cref="IMultiLevelCache"/> 默认实现 + 自定义 L1 容量策略（委托重载）。
    ///
    /// <paramref name="configure"/> 用于覆盖 <see cref="CachingOptions.Default"/> 中的 SizeLimit / CompactionPercentage。
    /// 用 Func&lt;,&gt; 而非 Action&lt;&gt; 让 lambda 能用 <c>with</c> 表达式（C# record）。
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddTenE0Caching(opts => opts with { SizeLimit = 64L * 1024 * 1024 });
    /// </code>
    /// </example>
    public static IServiceCollection AddTenE0Caching(
        this IServiceCollection services,
        Func<CachingOptions, CachingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = configure(CachingOptions.Default);
        return RegisterWithOptions(services, options);
    }

    /// <summary>
    /// 注册 <see cref="IMultiLevelCache"/> 默认实现 + 从 <see cref="IConfiguration"/> 绑定 L1 容量策略。
    ///
    /// 读取 <see cref="CachingOptions.SectionName"/> = "Caching" 节，期望结构：
    /// <code>
    /// {
    ///   "Caching": {
    ///     "SizeLimit": 33554432,
    ///     "CompactionPercentage": 0.10
    ///   }
    /// }
    /// </code>
    /// 未配置时退化为 <see cref="CachingOptions.Default"/>。
    /// </summary>
    public static IServiceCollection AddTenE0Caching(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // 1) IOptions<CachingOptions> 绑定到容器 —— 让业务方可通过 IOptions<CachingOptions> 解析
        services.Configure<CachingOptions>(configuration.GetSection(CachingOptions.SectionName));

        // 2) Func 重载走同一路径：把已绑定的 IOptions 解析后传入 configure
        return services.AddTenE0Caching(_ =>
        {
            // 内部即时解析（ServiceProvider 在 BuildServiceProvider 时才建，此处只能静态读取）
            // 走 ConfigurationBinder.Get<T>() 直接绑定 section，避免双重 IOptions 注册语义。
            var bound = new CachingOptions
            {
                SizeLimit = CachingOptions.DefaultSizeLimit,
                CompactionPercentage = 0.05,
            };
            configuration.GetSection(CachingOptions.SectionName).Bind(bound);
            return bound;
        });
    }

    private static IServiceCollection RegisterWithOptions(
        IServiceCollection services,
        CachingOptions options)
    {
        // L1：未显式注册时给个默认 MemoryCache。Singleton 是 MemoryCache 的标准生命周期。
        // #101: SizeLimit + CompactionPercentage 兜底 OOM —— 缺省会按 entry 数无限制堆积。
        services.TryAddSingleton<Microsoft.Extensions.Caching.Memory.IMemoryCache>(
            _ => new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions
                {
                    SizeLimit = options.SizeLimit,
                    CompactionPercentage = options.CompactionPercentage,
                }));

        // 默认实现 — 业务项目用 services.Replace(...) 覆盖
        services.TryAddSingleton<IMultiLevelCache, MultiLevelCache>();
        services.TryAddSingleton<IAtomicCounter, DistributedAtomicCounter>();
        return services;
    }
}
