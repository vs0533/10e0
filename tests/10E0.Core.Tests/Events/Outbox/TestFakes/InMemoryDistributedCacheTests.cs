using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using TenE0.Core.Caching;

namespace TenE0.Core.Tests.Events.Outbox.TestFakes;

/// <summary>
/// 验证 TestFakes 的 SETNX 原子语义 — 这是 Outbox 应用层锁（#82）测试可信度的基础。
///
/// <para>
/// <b>为什么必须有这些测试</b>：<see cref="L1L2CacheForTest"/> 和 <see cref="L2AtomicCounterForTest"/>
/// 在 PR #88 docker-integration-tests CI 上暴露过 TOCTOU race：早期实现用 "GetAsync + SetAsync" 序列
/// 模拟 SETNX，hostA 跑到 SetAsync 之前 hostB 也 Get 完成 → 两个都 success → 都 publish → exactly-once
/// 断言失败。修法是给 <see cref="InMemoryDistributedCache"/> 加 <c>TryAdd</c> 原子方法，让
/// <see cref="L1L2CacheForTest.TrySetAsync{T}"/> 走真原子路径。本测试类锁定原子语义，防回归。
/// </para>
/// </para>
/// </summary>
public sealed class InMemoryDistributedCacheTests
{
    [Fact]
    public void TryAdd_NewKey_ReturnsTrue()
    {
        var cache = new InMemoryDistributedCache();
        var ok = cache.TryAdd("k1", new byte[] { 1, 2, 3 },
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
        ok.Should().BeTrue("key 不存在时 TryAdd 必须成功");
        cache.Contains("k1").Should().BeTrue("写入后 key 必须可查");
    }

    [Fact]
    public void TryAdd_ExistingKey_ReturnsFalse()
    {
        var cache = new InMemoryDistributedCache();
        cache.Set("k1", new byte[] { 1 }, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
        var ok = cache.TryAdd("k1", new byte[] { 2 },
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
        ok.Should().BeFalse("key 已存在时 TryAdd 必须 false");
        // 验证原值未被覆盖
        var stored = cache.Get("k1");
        stored.Should().NotBeNull().And.Equal(new byte[] { 1 });
    }

    [Fact]
    public async Task L1L2CacheForTest_TrySetAsync_ConcurrentSameKey_OnlyOneSucceeds()
    {
        // 100 线程同时对同一 key 调 TrySetAsync — 必须恰好 1 个 success。
        // 早期 fake (PR #88) 用 read-then-write → race → 多个 success → exactly-once 失败。
        var l1 = new MemoryCache(new MemoryCacheOptions());
        var l2 = new InMemoryDistributedCache();
        var cache = new L1L2CacheForTest(l1, l2);

        const int parallel = 100;
        var results = new bool[parallel];
        Parallel.For(0, parallel, i =>
        {
            // 同步走 async path：GetAwaiter().GetResult() 在 InMemoryDistributedCache 上是 in-process，无死锁风险
            results[i] = cache.TrySetAsync<string>(
                "race-key",
                $"value-{i}",
                new CacheOptions { L1Duration = TimeSpan.FromMinutes(1), L2Duration = TimeSpan.FromMinutes(1) })
                .GetAwaiter().GetResult();
        });

        results.Count(r => r).Should().Be(
            1,
            "100 线程并发 SETNX 同一 key 必须恰好 1 个 success（SETNX 原子性）");

        // 验证 L2 存的是某个具体 value
        l2.Get("race-key").Should().NotBeNull("唯一 winner 写入的 value 必须存在");
    }

    [Fact]
    public async Task L1L2CacheForTest_TrySetAsync_DifferentKeys_AllSucceed()
    {
        var l1 = new MemoryCache(new MemoryCacheOptions());
        var l2 = new InMemoryDistributedCache();
        var cache = new L1L2CacheForTest(l1, l2);

        const int parallel = 50;
        var tasks = Enumerable.Range(0, parallel)
            .Select(i => cache.TrySetAsync<string>(
                $"key-{i}",
                $"value-{i}",
                new CacheOptions { L1Duration = TimeSpan.FromMinutes(1), L2Duration = TimeSpan.FromMinutes(1) }))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(r => r.Should().BeTrue("不同 key 并发 SETNX 必须各自 success"));
        l2.Count.Should().Be(parallel, "50 个不同 key 必须全部写入 L2");
    }
}
