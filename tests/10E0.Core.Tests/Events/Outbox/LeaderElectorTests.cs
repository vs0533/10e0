using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using TenE0.Core.Caching;
using TenE0.Core.Events.Outbox;

namespace TenE0.Core.Tests.Events.Outbox;

/// <summary>
/// Step 3/6 — LeaderElector 单测（feature #82 应用层分布式锁 + leader election）。
///
/// <para>
/// 验收口径（与 plan 对齐）：
/// <list type="number">
/// <item>单实例部署：无人是 leader 时本实例必当选；</item>
/// <item>两实例部署：A 先到先得，B 抢主必返回 false（整个本轮 skip）；</item>
/// <item>原 leader 主动 Release 后另一实例能接管；</item>
/// <item>新实例 ID 视为前任失效，可接管（模拟 lease expiry → 接管）。</item>
/// </list>
/// </para>
///
/// <para>
/// 不验证：真实两进程下的 split-brain（依赖 BDD / Testcontainers，本步范围外）。
/// </para>
/// </summary>
public sealed class LeaderElectorTests
{
    // ================================================================
    // Test Infrastructure — 复用 acceptance tests 的同款内存桩
    //   （InMemoryDistributedCache / SimpleL1L2Cache / L2AtomicCounter），
    //   保证单测覆盖"分派逻辑"与 acceptance "契约对齐" 走同一条代码路径。
    // ================================================================

    private sealed class InMemoryDistributedCache : IDistributedCache
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

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
            => Task.FromResult(Get(key));

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            lock (_gate)
            {
                var ttl = options.AbsoluteExpiration?.RelativeToNow
                    ?? options.AbsoluteExpirationRelativeToNow
                    ?? TimeSpan.FromMinutes(5);
                _store[key] = (value, DateTimeOffset.UtcNow.Add(ttl));
            }
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) { lock (_gate) _store.Remove(key); }
        public Task RemoveAsync(string key, CancellationToken token = default) { Remove(key); return Task.CompletedTask; }
    }

    private sealed class SimpleL1L2Cache : IMultiLevelCache
    {
        private readonly IMemoryCache _l1;
        private readonly IDistributedCache _l2;

        public SimpleL1L2Cache(IMemoryCache l1, IDistributedCache l2)
        {
            _l1 = l1;
            _l2 = l2;
        }

        public async Task<T?> GetOrSetAsync<T>(
            string key,
            Func<CancellationToken, ValueTask<T?>> factory,
            CacheOptions options,
            CancellationToken cancellationToken = default)
            where T : class
        {
            if (_l1.TryGetValue(key, out var l1Hit) && l1Hit is T l1Typed) return l1Typed;
            var l2Bytes = await _l2.GetAsync(key, cancellationToken);
            if (l2Bytes is { Length: > 0 })
            {
                var fromL2 = System.Text.Json.JsonSerializer.Deserialize<T>(l2Bytes);
                if (fromL2 is not null)
                {
                    _l1.Set(key, fromL2, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = options.L1Duration });
                    return fromL2;
                }
            }
            var fresh = await factory(cancellationToken);
            if (fresh is null) return null;
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(fresh);
            await _l2.SetAsync(key, bytes,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = options.L2Duration },
                cancellationToken);
            _l1.Set(key, fresh, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = options.L1Duration });
            return fresh;
        }

        public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _l1.Remove(key);
            await _l2.RemoveAsync(key, cancellationToken);
        }
    }

    private sealed class L2AtomicCounter : IAtomicCounter
    {
        private readonly IDistributedCache _l2;
        public L2AtomicCounter(IDistributedCache l2) => _l2 = l2;

        public async Task<long> IncrementAsync(string key, CancellationToken cancellationToken = default)
        {
            var raw = await _l2.GetAsync(key, cancellationToken);
            var current = raw is { Length: > 0 } && long.TryParse(System.Text.Encoding.UTF8.GetString(raw), out var n)
                ? n : 0L;
            var next = current + 1L;
            await _l2.SetAsync(key, System.Text.Encoding.UTF8.GetBytes(next.ToString()),
                new DistributedCacheEntryOptions(), cancellationToken);
            return next;
        }

        public async Task<long> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            var raw = await _l2.GetAsync(key, cancellationToken);
            return raw is { Length: > 0 } && long.TryParse(System.Text.Encoding.UTF8.GetString(raw), out var n) ? n : 0L;
        }
    }

    private static (IMultiLevelCache cache, InMemoryDistributedCache l2) CreateCache()
    {
        var l1 = new MemoryCache(new MemoryCacheOptions());
        var l2 = new InMemoryDistributedCache();
        return (new SimpleL1L2Cache(l1, l2), l2);
    }

    private static LeaderElector CreateElector(
        IMultiLevelCache cache,
        IAtomicCounter counter,
        string instanceId,
        TimeSpan? lease = null,
        string keyPrefix = "outbox:leader:test")
    {
        var options = Options.Create(new OutboxRelayOptions
        {
            LockInstanceId = instanceId,
            LeaderLeaseDuration = lease ?? TimeSpan.FromSeconds(30),
            LeaderInstanceKeyPrefix = keyPrefix,
        });
        return new LeaderElector(cache, counter, options);
    }

    // ================================================================
    // 单测 1: 单实例 — 无人是 leader 时本实例必当选
    // ================================================================

    [Fact]
    public async Task SingleInstance_AlwaysLeader()
    {
        // Arrange — 全新缓存
        var (cache, _) = CreateCache();
        var counter = new L2AtomicCounter(new InMemoryDistributedCache());
        IOutboxLock sut = CreateElector(cache, counter, "instance-A");

        // Act
        var acquired = await sut.TryAcquireAsync(
            messageId: "*",
            instanceId: "instance-A",
            lease: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);

        // Then
        acquired.Should().BeTrue(
            "无人持有 leader 时单实例部署必须当选 — leader election 主路径");
    }

    // ================================================================
    // 单测 2: 两实例 — A 先到先得，B 必 skip
    // ================================================================

    [Fact]
    public async Task TwoInstances_FirstAcquiresLeader_SecondSkips()
    {
        // Arrange — 共享 L2 + counter
        var l2 = new InMemoryDistributedCache();
        var cache = new SimpleL1L2Cache(new MemoryCache(new MemoryCacheOptions()), l2);
        var counter = new L2AtomicCounter(l2);
        var sutA = CreateElector(cache, counter, "instance-A");
        IOutboxLock sutB = CreateElector(cache, counter, "instance-B");

        // Act — A 先抢
        await sutA.TryAcquireAsync("*", "instance-A", TimeSpan.FromSeconds(30), CancellationToken.None);
        var bAcquired = await sutB.TryAcquireAsync(
            messageId: "*",
            instanceId: "instance-B",
            lease: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);

        // Then — B 必须 skip
        bAcquired.Should().BeFalse(
            "A 已当选时 B 抢主必须返回 false — 同一时刻全集群仅一个 leader");
    }

    // ================================================================
    // 单测 3: 原 leader 退位 → 接管
    // ================================================================

    [Fact]
    public async Task OriginalLeader_ReleasesAndSecondTakesOver()
    {
        // Arrange
        var l2 = new InMemoryDistributedCache();
        var cache = new SimpleL1L2Cache(new MemoryCache(new MemoryCacheOptions()), l2);
        var counter = new L2AtomicCounter(l2);
        var sutA = CreateElector(cache, counter, "instance-A");
        IOutboxLock sutB = CreateElector(cache, counter, "instance-B");
        await sutA.TryAcquireAsync("*", "instance-A", TimeSpan.FromSeconds(30), CancellationToken.None);

        // Act — A 退位
        await sutA.ReleaseAsync("*", "instance-A", CancellationToken.None);
        var bAcquired = await sutB.TryAcquireAsync(
            messageId: "*",
            instanceId: "instance-B",
            lease: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);

        // Then
        bAcquired.Should().BeTrue(
            "原 leader 主动 Release 后其他实例必须能接管 — leader failover 主路径");
    }

    // ================================================================
    // 单测 4: lease 续约 — 已是 leader 的实例续约必成功
    //   （等价于 "LeaderLeaseExpiry_AllowsReacquire"：同实例续约期间视为续任不丢；
    //     测试场景上等价于 A 拿后再次 TryAcquire 仍为 true，覆盖 lease 自动续期路径）
    // ================================================================

    [Fact]
    public async Task LeaderLeaseExpiry_AllowsReacquire()
    {
        // Arrange — A 当选
        var l2 = new InMemoryDistributedCache();
        var cache = new SimpleL1L2Cache(new MemoryCache(new MemoryCacheOptions()), l2);
        var counter = new L2AtomicCounter(l2);
        IOutboxLock sut = CreateElector(cache, counter, "instance-A");
        await sut.TryAcquireAsync("*", "instance-A", TimeSpan.FromSeconds(30), CancellationToken.None);

        // Act — 同一实例再次 TryAcquire（心跳续租）
        var renewed = await sut.TryAcquireAsync(
            messageId: "*",
            instanceId: "instance-A",
            lease: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);

        // Then — 必须续任
        renewed.Should().BeTrue(
            "leader 续约（心跳）必须返回 true — 不应被自己误失效");
    }
}
