namespace TenE0.Core.Caching;

/// <summary>
/// 多级缓存抽象 — L1 (进程内 IMemoryCache) + L2 (分布式 IDistributedCache) + 工厂回源。
///
/// 目的：
/// - 把 <see cref="Permissions.IPermissionCache"/> 等业务缓存从死绑 <c>IDistributedCache</c> 中解耦，
///   业务项目可注入自定义实现（如 .NET 8+ HybridCache、Memcached、多级链等）。
/// - 业务读路径只在 L1 miss 且 L2 miss 时才调用 factory；factory 内可走 DB / RPC / 计算。
///
/// 设计约束：
/// - 通用 GetOrSetAsync 而非独立的 Get/Set：减少业务方对"先查后写"竞态的关注；
///   impl 必须保证 L1+L2 填充的最终一致性由 L1 TTL 控制。
/// - RemoveAsync 必须同时清 L1 和 L2；impl 不可只清 L1（会导致 stale L2 命中回流污染 L1）。
/// </summary>
public interface IMultiLevelCache
{
    /// <summary>
    /// 取值；未命中调用 <paramref name="factory"/> 回源并写入 L1+L2。
    /// 实现应使用 <paramref name="options"/> 中的 L1 / L2 过期策略。
    /// </summary>
    /// <typeparam name="T">值类型。实现可用 JSON 序列化（System.Text.Json）。</typeparam>
    /// <param name="key">缓存 key，调用方负责命名空间唯一性（如 <c>"perm-role:v3:admin"</c>）。</param>
    /// <param name="factory">回源工厂；只在 L1+L2 双重 miss 时调用一次。</param>
    /// <param name="options">TTL / 过期策略。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<T?> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T?>> factory,
        CacheOptions options,
        CancellationToken cancellationToken = default)
        where T : class;

    /// <summary>从 L1 和 L2 同时移除指定 key。</summary>
    /// <param name="key">要失效的 key。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}
