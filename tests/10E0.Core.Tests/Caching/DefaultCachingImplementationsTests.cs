using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using TenE0.Core.Caching;

namespace TenE0.Core.Tests.Caching;

/// <summary>
/// #42: 默认实现的单元测试。
/// - <see cref="MultiLevelCache"/>：L1+L2+工厂回源的三级读路径
/// - <see cref="DistributedAtomicCounter"/>：原子自增 + 缺省值 0 行为
/// </summary>
[Trait("Category", "Unit")]
public sealed class DefaultCachingImplementationsTests
{
    // ────────────────────────────────────────────────────────────────────
    // MultiLevelCache
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MultiLevelCache_L1Hit_ReturnsFromMemoryWithoutTouchingL2()
    {
        var l1 = new MemoryCache(new MemoryCacheOptions());
        var l2 = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var sut = new MultiLevelCache(l1, l2);
        var opts = CacheOptions.Default;

        // 预填 L1
        l1.Set("k1", "cached_value", new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1),
        });

        var factoryCalls = 0;
        var result = await sut.GetOrSetAsync<string>("k1", _ =>
        {
            factoryCalls++;
            return ValueTask.FromResult<string?>("from_factory");
        }, opts);

        result.Should().Be("cached_value", "L1 命中应直接返回，不调用 factory 也不读 L2");
        factoryCalls.Should().Be(0);
    }

    [Fact]
    public async Task MultiLevelCache_L1MissL2Hit_PopulatesL1AndReturns()
    {
        var l1 = new MemoryCache(new MemoryCacheOptions());
        var l2 = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var sut = new MultiLevelCache(l1, l2);
        var opts = CacheOptions.Default;

        // 预填 L2
        await l2.SetAsync("k2", Encoding.UTF8.GetBytes("\"l2_value\""),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) });

        var factoryCalls = 0;
        var result = await sut.GetOrSetAsync<string>("k2", _ =>
        {
            factoryCalls++;
            return ValueTask.FromResult<string?>("from_factory");
        }, opts);

        result.Should().Be("l2_value", "L2 命中应反序列化返回");
        factoryCalls.Should().Be(0, "L2 命中不应调用 factory");
        // L1 现在应有此 key
        l1.TryGetValue("k2", out string? _).Should().BeTrue("L2 命中后应回填 L1");
    }

    [Fact]
    public async Task MultiLevelCache_L1AndL2Miss_CallsFactoryAndWritesBothLevels()
    {
        var l1 = new MemoryCache(new MemoryCacheOptions());
        var l2 = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var sut = new MultiLevelCache(l1, l2);
        var opts = CacheOptions.Default;

        var result = await sut.GetOrSetAsync<string>("k3", _ =>
            ValueTask.FromResult<string?>("fresh_value"), opts);

        result.Should().Be("fresh_value");

        // L1 应被填充
        l1.TryGetValue("k3", out var l1Value).Should().BeTrue();
        l1Value.Should().Be("fresh_value");
        // L2 应被填充
        var l2Bytes = await l2.GetAsync("k3");
        l2Bytes.Should().NotBeNull();
        Encoding.UTF8.GetString(l2Bytes!).Should().Be("\"fresh_value\"");
    }

    [Fact]
    public async Task MultiLevelCache_FactoryReturnsNull_DoesNotWriteCache()
    {
        var l1 = new MemoryCache(new MemoryCacheOptions());
        var l2 = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var sut = new MultiLevelCache(l1, l2);
        var opts = CacheOptions.Default;

        var result = await sut.GetOrSetAsync<string>("k4", _ =>
            ValueTask.FromResult<string?>(null), opts);

        result.Should().BeNull("factory 返回 null 时不应写入缓存");
        l1.TryGetValue("k4", out _).Should().BeFalse();
        (await l2.GetAsync("k4")).Should().BeNull();
    }

    [Fact]
    public async Task MultiLevelCache_RemoveAsync_ClearsBothLevels()
    {
        var l1 = new MemoryCache(new MemoryCacheOptions());
        var l2 = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var sut = new MultiLevelCache(l1, l2);
        var opts = CacheOptions.Default;

        // 填两层
        await sut.GetOrSetAsync<string>("k5", _ => ValueTask.FromResult<string?>("v"), opts);
        l1.TryGetValue("k5", out _).Should().BeTrue();
        (await l2.GetAsync("k5")).Should().NotBeNull();

        await sut.RemoveAsync("k5");

        l1.TryGetValue("k5", out _).Should().BeFalse("L1 必须清空");
        (await l2.GetAsync("k5")).Should().BeNull("L2 必须清空");
    }

    // ────────────────────────────────────────────────────────────────────
    // DistributedAtomicCounter
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DistributedAtomicCounter_IncrementFromZero_ReturnsOne()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var sut = new DistributedAtomicCounter(cache);

        var v = await sut.IncrementAsync("c1");

        v.Should().Be(1L, "key 不存在时应从 0 自增到 1");
    }

    [Fact]
    public async Task DistributedAtomicCounter_IncrementIsMonotonic()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var sut = new DistributedAtomicCounter(cache);

        var v1 = await sut.IncrementAsync("c2");
        var v2 = await sut.IncrementAsync("c2");
        var v3 = await sut.IncrementAsync("c2");

        v1.Should().Be(1L);
        v2.Should().Be(2L);
        v3.Should().Be(3L);
    }

    [Fact]
    public async Task DistributedAtomicCounter_GetAsync_NoValue_ReturnsZero()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var sut = new DistributedAtomicCounter(cache);

        var v = await sut.GetAsync("missing");

        v.Should().Be(0L);
    }

    [Fact]
    public async Task DistributedAtomicCounter_GetAsync_AfterIncrement_ReturnsCurrentValue()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var sut = new DistributedAtomicCounter(cache);

        await sut.IncrementAsync("c3");
        await sut.IncrementAsync("c3");
        var v = await sut.GetAsync("c3");

        v.Should().Be(2L);
    }

    /// <summary>
    /// #42 关键验证：并发 IncrementAsync 不能丢增。
    /// 单进程 MemoryDistributedCache 下 Sequential 调用即可模拟 — 多次自增后必须得到正确总数。
    /// </summary>
    [Fact]
    public async Task DistributedAtomicCounter_ConcurrentIncrements_NoLostIncrements()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var sut = new DistributedAtomicCounter(cache);
        const int tasks = 100;

        var finalValues = new ConcurrentBag<long>();
        var tasks2 = Enumerable.Range(0, tasks).Select(async _ =>
        {
            var v = await sut.IncrementAsync("concurrent_key");
            finalValues.Add(v);
        }).ToArray();

        await Task.WhenAll(tasks2);

        var distinct = finalValues.Distinct().OrderBy(x => x).ToList();
        distinct.Should().BeEquivalentTo(Enumerable.Range(1, tasks).Select(x => (long)x).ToList(),
            "100 次并发自增应得到 1..100 的完整单调序列，无丢增");
    }
}
