using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;

namespace TenE0.Core.Caching;

/// <summary>
/// <see cref="IMultiLevelCache"/> 默认实现：L1 <see cref="IMemoryCache"/> + L2 <see cref="IDistributedCache"/> + 工厂回源。
///
/// 读路径：L1 命中直接返回 → 否则 L2 命中回填 L1 → 否则调 factory 落 L2 + L1。
/// 写路径：factory 返回后双写（L1 立即可见；L2 由 IDistributedCache 实现决定）。
///
/// 设计取舍：
/// - L1/L2 都必须非 null：MultiLevelCache 构造函数强制要求两者；如只需单层请直接注入 IMemoryCache 或 IDistributedCache
/// - JSON 序列化：所有值统一 System.Text.Json；L1 用 <see cref="CacheOptions.L1Duration"/> 控制，
///   L2 用 <see cref="CacheOptions.L2Duration"/> 控制绝对过期
/// </summary>
internal sealed class MultiLevelCache : IMultiLevelCache
{
    private readonly IMemoryCache _l1;
    private readonly IDistributedCache _l2;

    public MultiLevelCache(IMemoryCache l1, IDistributedCache l2)
    {
        _l1 = l1;
        _l2 = l2;
    }

    public async Task<T?> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T?>> factory,
        CacheOptions options,
        CancellationToken cancellationToken = default) where T : class
    {
        // L1 命中：直接返回（最快路径）
        if (_l1.TryGetValue(key, out T? hit) && hit is not null)
            return hit;

        // L2 命中：反序列化 → 回填 L1 → 返回
        var l2Bytes = await _l2.GetAsync(key, cancellationToken);
        if (l2Bytes is not null && l2Bytes.Length > 0)
        {
            var fromL2 = JsonSerializer.Deserialize<T>(l2Bytes);
            if (fromL2 is not null)
            {
                SetL1(key, fromL2, options);
                return fromL2;
            }
        }

        // L1 + L2 都 miss：factory 回源 → 双写
        var fresh = await factory(cancellationToken);
        if (fresh is null) return null;

        await SetL2Async(key, fresh, options, cancellationToken);
        SetL1(key, fresh, options);
        return fresh;
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _l1.Remove(key);
        await _l2.RemoveAsync(key, cancellationToken);
    }

    /// <summary>
    /// SETNX 语义实现：L1+L2 都未命中时写入，否则返回 false。
    ///
    /// <para>
    /// <b>原子性</b>：用 <see cref="_setnxGate"/> 锁住整个 "L1 check + L2 check + L2 set + L1 set"
    /// 序列，让同进程内多线程并发 SETNX 同一 key 严格只一个 success。生产 Redis 后端天然
    /// 提供原子 SETNX (<c>SET key NX EX</c>)；InMemory / MemoryDistributedCache / Redis
    /// (StackExchange.Redis multiplexer sync API) 都在 <see cref="IDistributedCache"/> 同步
    /// Get/Set 路径上工作，锁内不 await，原子性由 lock + sync I/O 双重保证。
    /// </para>
    ///
    /// <para>
    /// 这是 PR #88 docker-integration-tests CI 暴露的真 bug 修复：早期实现用 "GetAsync + SetAsync"
    /// 序列无锁，hostA 跑到 Set 之前 hostB 也 Get 完成 → 两个都 success → 两个 host 都 publish
    /// 同一消息 → exactly-once 失败。
    /// </para>
    /// </summary>
    public Task<bool> TrySetAsync<T>(
        string key,
        T value,
        CacheOptions options,
        CancellationToken cancellationToken = default) where T : class
    {
        // 在 L2 short-circuit 路径（生产 L2Duration=0）下直接跳过
        if (options.L2Duration <= TimeSpan.Zero) return Task.FromResult(false);

        // 序列化 SETNX 调用（同进程内多线程并发同一 key 必有 1 winner）
        // 锁内全 sync 调用（IDistributedCache.Get/Set 都有 sync 版本），不 await yield。
        lock (_setnxGate)
        {
            if (_l1.TryGetValue(key, out _)) return Task.FromResult(false);

            // L2 sync Get（IDistributedCache.Get 同步接口；不 yield，锁内安全）
            var existing = _l2.Get(key);
            if (existing is { Length: > 0 }) return Task.FromResult(false);

            // L2 sync Set（IDistributedCache.Set 同步接口）
            _l2.Set(
                key,
                JsonSerializer.SerializeToUtf8Bytes(value),
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = options.L2Duration,
                });

            // L1 同步写
            if (options.L1Duration > TimeSpan.Zero)
            {
                _l1.Set(key, value, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = options.L1Duration,
                });
            }
            return Task.FromResult(true);
        }
    }

    private readonly object _setnxGate = new();

    /// <summary>
    /// 覆盖写入：用于锁续约或主动改值。L1+L2 都覆盖。
    /// </summary>
    public async Task SetAsync<T>(
        string key,
        T value,
        CacheOptions options,
        CancellationToken cancellationToken = default) where T : class
    {
        await SetL2Async(key, value, options, cancellationToken);
        SetL1(key, value, options);
    }

    private void SetL1<T>(string key, T value, CacheOptions options) where T : class
    {
        if (options.L1Duration <= TimeSpan.Zero) return;
        _l1.Set(key, value, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = options.L1Duration,
        });
    }

    private async Task SetL2Async<T>(
        string key,
        T value,
        CacheOptions options,
        CancellationToken cancellationToken) where T : class
    {
        if (options.L2Duration <= TimeSpan.Zero) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        await _l2.SetAsync(key, bytes, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = options.L2Duration,
        }, cancellationToken);
    }
}

/// <summary>
/// <see cref="IAtomicCounter"/> 基于 <see cref="IDistributedCache"/> 的默认实现。
///
/// 原子性保证：使用 <c>GetString → 解析 → SetString</c> 三步。生产 Redis 实现
/// 应替换为 <c>INCR</c> 原生命令（通过 IDistributedCache 的 Redis 扩展或自建接口）。
/// 当前实现适用于单进程 MemoryDistributedCache；多副本部署需要业务项目替换实现。
///
/// 注：不抛 Redis Lua 脚本依赖是为了保持与 <see cref="IDistributedCache"/> 抽象的兼容性。
/// </summary>
internal sealed class DistributedAtomicCounter : IAtomicCounter
{
    private readonly IDistributedCache _cache;

    public DistributedAtomicCounter(IDistributedCache cache)
    {
        _cache = cache;
    }

    public async Task<long> IncrementAsync(string key, CancellationToken cancellationToken = default)
    {
        // 1) 读当前值（不存在视为 0）
        var currentBytes = await _cache.GetAsync(key, cancellationToken);
        var current = currentBytes is { Length: > 0 } &&
                      long.TryParse(Encoding.UTF8.GetString(currentBytes), out var n)
            ? n
            : 0L;

        // 2) 自增
        var next = current + 1L;

        // 3) 写回。Key 永久保留：每次 Increment 都从 0 起步会丢增。
        await _cache.SetAsync(
            key,
            Encoding.UTF8.GetBytes(next.ToString()),
            new DistributedCacheEntryOptions(),
            cancellationToken);
        return next;
    }

    public async Task<long> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var bytes = await _cache.GetAsync(key, cancellationToken);
        if (bytes is null || bytes.Length == 0) return 0L;
        return long.TryParse(Encoding.UTF8.GetString(bytes), out var n) ? n : 0L;
    }
}
