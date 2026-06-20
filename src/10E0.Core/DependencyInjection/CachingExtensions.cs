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
    /// - <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/>：通常由 AddTenE0Core() 注入
    /// </summary>
    public static IServiceCollection AddTenE0Caching(this IServiceCollection services)
    {
        // L1：未显式注册时给个默认 MemoryCache。Singleton 是 MemoryCache 的标准生命周期。
        services.TryAddSingleton<Microsoft.Extensions.Caching.Memory.IMemoryCache>(
            _ => new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));

        // 默认实现 — 业务项目用 services.Replace(...) 覆盖
        services.TryAddSingleton<IMultiLevelCache, MultiLevelCache>();
        services.TryAddSingleton<IAtomicCounter, DistributedAtomicCounter>();
        return services;
    }
}
