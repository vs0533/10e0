using Microsoft.Extensions.Caching.Distributed;

namespace TenE0.Core.Tests.Events.Outbox.TestFakes;

/// <summary>
/// 单进程 in-memory <see cref="IDistributedCache"/> 实现 —— 用于 Outbox 应用层锁测试
/// （#82 Distributed/Leader 模式单进程验证不依赖真实 Redis）。
///
/// <para>
/// 与 Microsoft.Extensions.Caching.Distributed.MemoryDistributedCache 的差异：
/// 本类暴露 <c>Count</c> / <c>Contains</c> 等 inspect 能力，方便测试断言"key 已存在"等状态。
/// 生产代码请用官方 MemoryDistributedCache。
/// </para>
/// </summary>
public sealed class InMemoryDistributedCache : IDistributedCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (byte[] Value, DateTimeOffset ExpiresAt)> _store = new(StringComparer.Ordinal);

    public byte[]? Get(string key)
    {
        lock (_gate)
        {
            if (!_store.TryGetValue(key, out var entry)) return null;
            if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _store.Remove(key);
                return null;
            }
            return entry.Value;
        }
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
        await Task.FromResult(Get(key)).ConfigureAwait(false);

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        var ttl = options?.AbsoluteExpirationRelativeToNow ?? options?.SlidingExpiration ?? TimeSpan.FromMinutes(5);
        lock (_gate)
        {
            _store[key] = (value, DateTimeOffset.UtcNow + ttl);
        }
    }

    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        Set(key, value, options);
        await Task.CompletedTask;
    }

    /// <summary>
    /// 测试用原子 SETNX —— key 不存在时写入并返回 true；存在时返回 false。
    /// 用于让 <see cref="L1L2CacheForTest"/> / <see cref="L2AtomicCounterForTest"/> 的 check-then-set
    /// 序列在同一进程内多线程并发下也能模拟生产 Redis SETNX 的原子语义。
    /// （PR #88 docker-integration-tests CI 教训：早期 fake 用 read-then-write → TOCTOU race →
    /// 两个 host 都 TrySetAsync 成功 → 都 publish 同一消息 2 次 → exactly-once 断言失败。）
    /// </summary>
    public bool TryAdd(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        var ttl = options?.AbsoluteExpirationRelativeToNow ?? options?.SlidingExpiration ?? TimeSpan.FromMinutes(5);
        lock (_gate)
        {
            if (_store.TryGetValue(key, out var existing) && existing.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return false; // key 存在且未过期 → 不可 add
            }
            _store[key] = (value, DateTimeOffset.UtcNow + ttl);
            return true;
        }
    }

    public void Refresh(string key)
    {
        lock (_gate)
        {
            if (_store.TryGetValue(key, out var entry))
                _store[key] = (entry.Value, entry.ExpiresAt); // 简化：不滑动延长
        }
    }

    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        Refresh(key);
        await Task.CompletedTask;
    }

    public void Remove(string key)
    {
        lock (_gate) { _store.Remove(key); }
    }

    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        Remove(key);
        await Task.CompletedTask;
    }

    /// <summary>测试断言用：当前 store 中 key 数量。</summary>
    public int Count
    {
        get { lock (_gate) return _store.Count; }
    }

    /// <summary>测试断言用：key 当前是否存在（且未过期）。</summary>
    public bool Contains(string key) => Get(key) is not null;
}
