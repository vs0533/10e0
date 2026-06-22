using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using TenE0.Core.Caching;
using TenE0.Core.Events.Outbox;

namespace TenE0.Core.Tests.Events.Outbox;

/// <summary>
/// 单元测试 — <see cref="DistributedOutboxLock"/>（基于 <see cref="IMultiLevelCache"/> L2 的应用层分布式锁）
///
/// <para>
/// <b>范围</b>：本测试套件只覆盖"单进程内存路径"下的 compare-and-set / ownership 语义。
/// 真实多实例并发的"每消息恰好一次"由 <c>DistributedOutboxLockAcceptanceTests</c>
/// （Step 5/6 Testcontainers BDD）兜底。
/// </para>
///
/// <para>
/// <b>场景清单</b>：
/// <list type="number">
/// <item>首实例 TryAcquire → true</item>
/// <item>他实例 TryAcquire（租约内）→ false</item>
/// <item>同实例 TryAcquire（租约到期后）→ true（renew 语义）</item>
/// <item>他实例 Release → no-op</item>
/// <item>本实例 Release → L1+L2 都清掉</item>
/// </list>
/// </para>
/// </summary>
public sealed class DistributedOutboxLockTests
{
    // ================================================================
    // Test infrastructure — 自建最小 InMemory IDistributedCache
    // ================================================================

    /// <summary>
    /// 进程内 <see cref="IDistributedCache"/> — 单测用，线程安全，支持绝对过期。
    /// </summary>
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
                var absolute = options.AbsoluteExpiration?.RelativeToNow
                    ?? options.AbsoluteExpirationRelativeToNow
                    ?? TimeSpan.FromMinutes(5);
                _store[key] = (value, DateTimeOffset.UtcNow.Add(absolute));
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

    /// <summary>
    /// 自建 IMultiLevelCache 透传 — 跟 DistributedOutboxLockAcceptanceTests 的
    /// MultiLevelCacheForTest 同款形态（避免依赖 internal MultiLevelCache）。
    /// </summary>
    private sealed class TestMultiLevelCache : IMultiLevelCache
    {
        private readonly IMemoryCache _l1;
        private readonly IDistributedCache _l2;

        public TestMultiLevelCache(IMemoryCache l1, IDistributedCache l2)
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
            if (_l1.TryGetValue(key, out var l1Hit) && l1Hit is T l1Typed)
                return l1Typed;

            var l2Bytes = await _l2.GetAsync(key, cancellationToken);
            if (l2Bytes is { Length: > 0 })
            {
                var fromL2 = System.Text.Json.JsonSerializer.Deserialize<T>(l2Bytes);
                if (fromL2 is not null)
                {
                    _l1.Set(key, fromL2, new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = options.L1Duration,
                    });
                    return fromL2;
                }
            }

            var fresh = await factory(cancellationToken);
            if (fresh is null) return null;
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(fresh);
            await _l2.SetAsync(key, bytes,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = options.L2Duration },
                cancellationToken);
            _l1.Set(key, fresh, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = options.L1Duration,
            });
            return fresh;
        }

        public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _l1.Remove(key);
            await _l2.RemoveAsync(key, cancellationToken);
        }
    }

    private static (IMultiLevelCache cache, IMemoryCache l1, InMemoryDistributedCache l2) CreateCache()
    {
        var l1 = new MemoryCache(new MemoryCacheOptions());
        var l2 = new InMemoryDistributedCache();
        return (new TestMultiLevelCache(l1, l2), l1, l2);
    }

    private static DistributedOutboxLock CreateLock(
        IMultiLevelCache cache,
        string instanceId = "instance-A",
        TimeSpan? lease = null)
    {
        var options = Options.Create(new OutboxRelayOptions
        {
            LockInstanceId = instanceId,
            LockLeaseDuration = lease ?? TimeSpan.FromSeconds(30),
        });
        return new DistributedOutboxLock(cache, options);
    }

    // ================================================================
    // Scenario 1: 首次获取 — 无人持锁时本实例必拿
    // ================================================================

    [Fact]
    public async Task TryAcquire_FirstInstance_ReturnsTrue()
    {
        // Arrange — 全新缓存
        var (cache, _, _) = CreateCache();
        IOutboxLock sut = CreateLock(cache, instanceId: "instance-A");

        // Act
        var acquired = await sut.TryAcquireAsync(
            messageId: "msg-1",
            instanceId: "instance-A",
            lease: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);

        // Then
        acquired.Should().BeTrue(
            "无人持锁时本实例 TryAcquire 必须返回 true — compare-and-set 主路径");
    }

    // ================================================================
    // Scenario 2: 互斥语义 — 租约未到期时他实例 TryAcquire 必须 false
    // ================================================================

    [Fact]
    public async Task TryAcquire_DifferentInstance_ReturnsFalse_WhenLeaseActive()
    {
        // Arrange — instance-A 先持锁
        var (cache, _, _) = CreateCache();
        var sutA = CreateLock(cache, instanceId: "instance-A");
        var sutB = CreateLock(cache, instanceId: "instance-B");
        await sutA.TryAcquireAsync("msg-1", "instance-A", TimeSpan.FromSeconds(30), CancellationToken.None);

        // Act — instance-B 来抢
        var acquired = await sutB.TryAcquireAsync(
            messageId: "msg-1",
            instanceId: "instance-B",
            lease: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);

        // Then
        acquired.Should().BeFalse(
            "租约未到期时他实例 TryAcquire 必须返回 false — 这是 #74 已知风险 #1 的核心防护");
    }

    // ================================================================
    // Scenario 3: 续约语义 — 同实例在租约到期后再次 TryAcquire 必须 true
    //   (issue body 明确语义："自持自取允许覆盖 = 续约")
    // ================================================================

    [Fact]
    public async Task TryAcquire_SameInstance_AfterLeaseExpiry_ReturnsTrue_AsRenew()
    {
        // Arrange — instance-A 持锁，租约设 50ms 让其在测试中自然过期
        var (cache, _, l2) = CreateCache();
        IOutboxLock sut = CreateLock(
            cache,
            instanceId: "instance-A",
            lease: TimeSpan.FromMilliseconds(50));
        await sut.TryAcquireAsync("msg-1", "instance-A", TimeSpan.FromMilliseconds(50), CancellationToken.None);

        // 等到 L2 过期（同时把 L1 entry 也清掉，避免命中 L1 路径走"自持续约"旁路；
        // 真实生产中 L1 短过期策略就是这种情况）
        await Task.Delay(80);

        // Act — 同实例再来一次（应当走"锁已过期 → 重新抢占"路径，Renew 语义）
        var reacquired = await sut.TryAcquireAsync(
            messageId: "msg-1",
            instanceId: "instance-A",
            lease: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);

        // Then
        reacquired.Should().BeTrue(
            "issue body 明确续约语义：本实例在原锁过期后再次 TryAcquire 必须返回 true");

        // 同时验证 L2 实际重新写入了 instance-A（Renew 落地）。
        // 注意：IMultiLevelCache.GetOrSetAsync<string> 内部用 JSON 序列化 owner 后写 L2，
        // 所以读取要走"反序列化"路径（与生产路径一致），而不是 raw bytes + UTF8.GetString —
        // 后者会拿到带 JSON 引号的 "\"instance-A\""，与 owner string "instance-A" 不相等。
        var key = "outbox:lock:msg-1";
        var stored = await l2.GetAsync(key);
        stored.Should().NotBeNull("Renew 后 L2 必须重新写入 instance-A");
        var storedOwner = System.Text.Json.JsonSerializer.Deserialize<string>(stored!);
        storedOwner.Should().Be("instance-A",
            "L2 重新写入的 owner 必须仍是 instance-A（续约语义）");
    }

    // ================================================================
    // Scenario 4: Release — 他实例 Release 必须 no-op（不抛异常 + L2 不变）
    // ================================================================

    [Fact]
    public async Task Release_DifferentInstance_IsNoOp()
    {
        // Arrange — instance-B 持锁
        var (cache, _, l2) = CreateCache();
        var sutB = CreateLock(cache, instanceId: "instance-B");
        IOutboxLock sutA = CreateLock(cache, instanceId: "instance-A");
        await sutB.TryAcquireAsync("msg-1", "instance-B", TimeSpan.FromSeconds(30), CancellationToken.None);

        // Act — instance-A 误调 Release
        Func<Task> act = () => sutA.ReleaseAsync("msg-1", "instance-A", CancellationToken.None);

        // Then — 不抛异常 + L2 仍由 B 持有
        await act.Should().NotThrowAsync(
            "Release 必须幂等，他人持有时调 Release 不能抛异常");
        var key = "outbox:lock:msg-1";
        var remaining = await l2.GetAsync(key);
        remaining.Should().NotBeNull(
            "所有权校验：instance-A 不能误删 instance-B 的锁键");
        // L2 存的是 JSON 序列化的 owner string，需要反序列化后再比对 —
        // 见 TryAcquire_SameInstance_AfterLeaseExpiry_ReturnsTrue_AsRenew 同款解释。
        System.Text.Json.JsonSerializer.Deserialize<string>(remaining!).Should().Be("instance-B",
            "Release 必须按 instanceId 校验所有权，仅当匹配时才清空");
    }

    // ================================================================
    // Scenario 5: Release — 本实例 Release 后 L1 和 L2 都清空
    // ================================================================

    [Fact]
    public async Task Release_SameInstance_RemovesFromL1AndL2()
    {
        // Arrange — instance-A 持锁
        var (cache, l1, l2) = CreateCache();
        IOutboxLock sut = CreateLock(cache, instanceId: "instance-A");
        await sut.TryAcquireAsync("msg-1", "instance-A", TimeSpan.FromSeconds(30), CancellationToken.None);

        var key = "outbox:lock:msg-1";
        // 验证前置状态：L1 和 L2 都有
        l1.TryGetValue(key, out _).Should().BeTrue("持锁后 L1 必有值");
        l2.Get(key).Should().NotBeNull("持锁后 L2 必有值");

        // Act — 本实例 Release
        await sut.ReleaseAsync("msg-1", "instance-A", CancellationToken.None);

        // Then — L1 + L2 都被清空
        l1.TryGetValue(key, out _).Should().BeFalse(
            "Release 必须同时清空 L1（否则下次 TryAcquire 会命中 stale L1 假阳性续约）");
        l2.Get(key).Should().BeNull(
            "Release 必须同时清空 L2，让其他实例下轮 TryAcquire 能重新拿到锁");
    }
}
