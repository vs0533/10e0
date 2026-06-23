using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using TenE0.Core.Abstractions;
using TenE0.Core.Caching;
using TenE0.Core.Permissions;

namespace TenE0.Core.Tests.Permissions;

[Trait("Category", "Unit")]
public sealed class DistributedPermissionCacheTests
{
    private static PermissionsOptions DefaultOptions => new()
    {
        SuperUserRoles = new HashSet<string>(StringComparer.Ordinal),
        CacheDuration = TimeSpan.FromMinutes(5)
    };

    // #37: 默认 namespace 与遗留 hardcoded 字面量逐字一致 —— 测试可直接复用 legacy literal。
    private static ICacheKeyNamespace DefaultNamespace() => new DefaultCacheKeyNamespace();

    /// <summary>
    /// #42: DistributedPermissionCache 现在通过 IAtomicCounter 读/写版本号。
    /// 替换旧版"直接用 IDistributedCache.GetAsync('perm-cache:version')"的写法。
    /// </summary>
    private static Mock<IAtomicCounter> CreateCounter(long currentVersion)
    {
        var counter = new Mock<IAtomicCounter>();
        // #37: counter key 走 ICacheKeyNamespace，多租户场景下 key 形如 "{tenant}:perm-cache:version"。
        // 测试 stub 用 EndsWith 兜底，让单租户 / 多租户两种 namespace 都能匹配到 stub。
        counter.Setup(c => c.GetAsync(It.Is<string>(k => k.EndsWith("perm-cache:version")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(currentVersion);
        return counter;
    }

    [Fact]
    public async Task GetRolePermissionsAsync_CacheHit_ReturnsDeserializedSet()
    {
        var cacheMock = new Mock<IDistributedCache>();
        var permissions = new HashSet<string> { "user.read", "user.write" };
        var json = JsonSerializer.Serialize(permissions);
        // IAtomicCounter.GetAsync("perm-cache:version") → 0
        var counter = CreateCounter(0);
        // Then reads the role permissions
        cacheMock.Setup(c => c.GetAsync("perm-role:v0:admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Encoding.UTF8.GetBytes(json));

        var sut = new DistributedPermissionCache(cacheMock.Object, counter.Object, DefaultNamespace(), Options.Create(DefaultOptions));
        var result = await sut.GetRolePermissionsAsync("admin");

        result.Should().NotBeNull();
        result!.Should().BeEquivalentTo(permissions);
    }

    [Fact]
    public async Task GetRolePermissionsAsync_CacheMiss_ReturnsNull()
    {
        var cacheMock = new Mock<IDistributedCache>();
        cacheMock.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);
        var counter = CreateCounter(0);

        var sut = new DistributedPermissionCache(cacheMock.Object, counter.Object, DefaultNamespace(), Options.Create(DefaultOptions));
        var result = await sut.GetRolePermissionsAsync("viewer");

        result.Should().BeNull();
    }

    [Fact]
    public async Task SetRolePermissionsAsync_SerializesAndStores()
    {
        var cacheMock = new Mock<IDistributedCache>();
        var counter = CreateCounter(0);
        var sut = new DistributedPermissionCache(cacheMock.Object, counter.Object, DefaultNamespace(), Options.Create(DefaultOptions));

        await sut.SetRolePermissionsAsync("admin", new HashSet<string> { "perm.a" });

        cacheMock.Verify(
            c => c.SetAsync(
                It.Is<string>(k => k.StartsWith("perm-role:v0:admin")),
                It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InvalidateRoleAsync_RemovesCacheKey()
    {
        var cacheMock = new Mock<IDistributedCache>();
        var counter = CreateCounter(0);
        var sut = new DistributedPermissionCache(cacheMock.Object, counter.Object, DefaultNamespace(), Options.Create(DefaultOptions));

        await sut.InvalidateRoleAsync("admin");

        cacheMock.Verify(c => c.RemoveAsync("perm-role:v0:admin", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InvalidateAllAsync_DelegatesToAtomicCounter_Increment()
    {
        // #42: InvalidateAllAsync 现在通过 IAtomicCounter.IncrementAsync 原子自增版本号
        // 替代旧的 GetString → Parse → +1 → SetString 非原子三步。
        var cacheMock = new Mock<IDistributedCache>();
        var counter = new Mock<IAtomicCounter>();
        counter.Setup(c => c.IncrementAsync("perm-cache:version", It.IsAny<CancellationToken>()))
            .ReturnsAsync(4L);
        var sut = new DistributedPermissionCache(cacheMock.Object, counter.Object, DefaultNamespace(), Options.Create(DefaultOptions));

        await sut.InvalidateAllAsync();

        counter.Verify(c => c.IncrementAsync("perm-cache:version", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BuildKeyAsync_UsesCounterVersion()
    {
        // 版本号从 counter 取，不是 IDistributedCache
        var cacheMock = new Mock<IDistributedCache>();
        var counter = CreateCounter(7);
        var sut = new DistributedPermissionCache(cacheMock.Object, counter.Object, DefaultNamespace(), Options.Create(DefaultOptions));

        await sut.InvalidateRoleAsync("editor");

        cacheMock.Verify(c => c.RemoveAsync("perm-role:v7:editor", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── #114: schema drift 防御（升级部署场景下旧 JSON 不让缓存层持续抛异常） ──

    [Fact]
    public async Task GetRolePermissionsAsync_ReturnsNullAndEvictsBadJson_WhenStoredJsonIsMalformed()
    {
        // Arrange — 缓存里是损坏 JSON（升级到一半、并发写入等场景）
        var cacheMock = new Mock<IDistributedCache>();
        cacheMock.Setup(c => c.GetAsync("perm-role:v0:admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Encoding.UTF8.GetBytes("{ this is not valid json"));
        var counter = CreateCounter(0);
        var sut = new DistributedPermissionCache(cacheMock.Object, counter.Object, DefaultNamespace(), Options.Create(DefaultOptions));

        // Act
        var result = await sut.GetRolePermissionsAsync("admin");

        // Assert — 不抛异常；返回 null（与"未命中"语义一致，PermissionEvaluator 会回源 DB）
        result.Should().BeNull(
            "malformed cached JSON must surface as a cache miss, not bubble a JsonException to the caller");
        // 坏缓存必须被清除，否则下次查询仍会命中同一个坏值
        cacheMock.Verify(
            c => c.RemoveAsync("perm-role:v0:admin", It.IsAny<CancellationToken>()),
            Times.Once,
            "a malformed cache entry must be evicted so the next read does not re-throw the same JsonException");
    }

    [Fact]
    public async Task GetRolePermissionsAsync_ReturnsNullAndEvictsBadJson_WhenStoredJsonIsLegacySchema()
    {
        // Arrange — 缓存里是 JSON 但根类型错配：单 string 根（合法 JSON），
        // 反序列化成 HashSet<string> 必然失败（root kind mismatch），
        // 在 reflection / source-generator 路径下都抛 JsonException 或 NotSupportedException。
        var cacheMock = new Mock<IDistributedCache>();
        cacheMock.Setup(c => c.GetAsync("perm-role:v0:admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Encoding.UTF8.GetBytes("\"not-a-hashset\""));
        var counter = CreateCounter(0);
        var sut = new DistributedPermissionCache(cacheMock.Object, counter.Object, DefaultNamespace(), Options.Create(DefaultOptions));

        // Act
        var result = await sut.GetRolePermissionsAsync("admin");

        // Assert — type mismatch 必须被吞掉，调用方拿到 null，让 evaluator 走 store 重读
        result.Should().BeNull(
            "deserialize failures on a legacy/foreign schema must be treated as a cache miss");
        cacheMock.Verify(
            c => c.RemoveAsync("perm-role:v0:admin", It.IsAny<CancellationToken>()),
            Times.Once,
            "the bad cache entry must be evicted on the same request so subsequent requests do not re-throw");
    }

    [Fact]
    public async Task GetRolePermissionsAsync_ReturnsNull_WhenEvictionFails()
    {
        // Arrange — 反序列化失败 + 清缓存也失败（如 Redis 瞬断），
        // GetRolePermissionsAsync 必须仍返回 null，不能把清理异常冒泡到 HasAsync。
        var cacheMock = new Mock<IDistributedCache>();
        cacheMock.Setup(c => c.GetAsync("perm-role:v0:admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Encoding.UTF8.GetBytes("{ malformed"));
        cacheMock.Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated Redis transient"));
        var counter = CreateCounter(0);
        var sut = new DistributedPermissionCache(cacheMock.Object, counter.Object, DefaultNamespace(), Options.Create(DefaultOptions));

        // Act
        var result = await sut.GetRolePermissionsAsync("admin");

        // Assert — 清理失败不阻塞回退路径
        result.Should().BeNull(
            "eviction failure must not prevent the caller from falling back to the store");
    }

    // ── #37: 多租户 namespace 隔离 ────────────────────────────────

    [Fact]
    public async Task GivenCustomTenantNamespace_WhenInvalidatingAll_ThenCounterKeyContainsTenantPrefix()
    {
        // Arrange — 多租户共享 Redis：namespace 拼 tenant 前缀
        var counter = new Mock<IAtomicCounter>();
        var sut = new DistributedPermissionCache(
            new Mock<IDistributedCache>().Object,
            counter.Object,
            new DefaultCacheKeyNamespace(tenantId: "acme"),
            Options.Create(DefaultOptions));

        // Act
        await sut.InvalidateAllAsync();

        // Assert — counter 收到的 key 必须带 "acme:" 顶级前缀，避免跨租户串
        counter.Verify(
            c => c.IncrementAsync("acme:perm-cache:version", It.IsAny<CancellationToken>()),
            Times.Once,
            "InvalidateAllAsync must route through ICacheKeyNamespace so multi-tenant deployments do not share counter keys");
    }

    [Fact]
    public async Task GivenCustomTenantNamespace_WhenBuildingRoleKey_ThenRoleKeyStartsWithTenantPrefix()
    {
        // Arrange
        var cacheMock = new Mock<IDistributedCache>();
        var counter = CreateCounter(3);
        var sut = new DistributedPermissionCache(
            cacheMock.Object,
            counter.Object,
            new DefaultCacheKeyNamespace(tenantId: "globex"),
            Options.Create(DefaultOptions));

        // Act
        await sut.InvalidateRoleAsync("editor");

        // Assert — role key 也带 tenant 前缀
        cacheMock.Verify(
            c => c.RemoveAsync("globex:perm-role:v3:editor", It.IsAny<CancellationToken>()),
            Times.Once,
            "tenant-scoped role keys prevent cross-tenant permission cache pollution");
    }
}
