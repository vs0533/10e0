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

    // ── #98: DistributedAtomicCounter 非原子警告 ───────────────────────────────

    [Fact]
    public void DistributedAtomicCounter_Xmldoc_WarnsAboutMultiReplicaRace()
    {
        // #98: 测试真读 xmldoc，断言每个 MultiReplicaRaceWarningKeywords 关键字都出现在
        // DistributedAtomicCounter 类 xmldoc summary 文本里。这才能阻止"删除 xmldoc 警告
        // + 保留 const 数组"这种 trivially-passing 的回归（review bot 抓到）。
        var xml = GetXmlDoc(typeof(DistributedAtomicCounter));
        xml.Should().NotBeNullOrEmpty(
            "10E0.Core.xml 必须在 src/test bin 中存在；如丢失，检查 Directory.Build.props 的 GenerateDocumentationFile=true");
        DistributedAtomicCounter.MultiReplicaRaceWarningKeywords
            .Should().NotBeEmpty("MultiReplicaRaceWarningKeywords 是 #98 警告契约的代码化事实");
        foreach (var keyword in DistributedAtomicCounter.MultiReplicaRaceWarningKeywords)
        {
            xml.Should().Contain(keyword,
                $"DistributedAtomicCounter xmldoc 必须包含关键字 \"{keyword}\"——防止警告被静默删除");
        }
    }

    private static string GetXmlDoc(Type type)
    {
        // type.Assembly.Location 在 dotnet test 下指向 test bin；10E0.Core.xml 已被 SDK copy 到 test bin。
        // 单文件发布 / trim 模式下 Location 可能返回空串，防御一下。
        var asmPath = type.Assembly.Location;
        if (string.IsNullOrEmpty(asmPath)) return string.Empty;
        var xmlPath = Path.ChangeExtension(asmPath, ".xml");
        if (!File.Exists(xmlPath)) return string.Empty;

        // XDocument（C# 14 / .NET 10 习惯）。返回 member.ToString() —— summary 文本里
        // 已含被断言的 race/INCR/Replace 三个关键字，不需要单独提取 <see cref> 属性值。
        var doc = System.Xml.Linq.XDocument.Load(xmlPath);
        var member = doc.Root?
            .Element("members")?
            .Elements("member")
            .FirstOrDefault(m => m.Attribute("name")?.Value == $"T:{type.FullName}");
        return member?.ToString() ?? string.Empty;
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
