using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
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

    /// <summary>
    /// #42: DistributedPermissionCache 现在通过 IAtomicCounter 读/写版本号。
    /// 替换旧版"直接用 IDistributedCache.GetAsync('perm-cache:version')"的写法。
    /// </summary>
    private static Mock<IAtomicCounter> CreateCounter(long currentVersion)
    {
        var counter = new Mock<IAtomicCounter>();
        counter.Setup(c => c.GetAsync("perm-cache:version", It.IsAny<CancellationToken>()))
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

        var sut = new DistributedPermissionCache(cacheMock.Object, counter.Object, Options.Create(DefaultOptions));
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

        var sut = new DistributedPermissionCache(cacheMock.Object, counter.Object, Options.Create(DefaultOptions));
        var result = await sut.GetRolePermissionsAsync("viewer");

        result.Should().BeNull();
    }

    [Fact]
    public async Task SetRolePermissionsAsync_SerializesAndStores()
    {
        var cacheMock = new Mock<IDistributedCache>();
        var counter = CreateCounter(0);
        var sut = new DistributedPermissionCache(cacheMock.Object, counter.Object, Options.Create(DefaultOptions));

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
        var sut = new DistributedPermissionCache(cacheMock.Object, counter.Object, Options.Create(DefaultOptions));

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
        var sut = new DistributedPermissionCache(cacheMock.Object, counter.Object, Options.Create(DefaultOptions));

        await sut.InvalidateAllAsync();

        counter.Verify(c => c.IncrementAsync("perm-cache:version", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BuildKeyAsync_UsesCounterVersion()
    {
        // 版本号从 counter 取，不是 IDistributedCache
        var cacheMock = new Mock<IDistributedCache>();
        var counter = CreateCounter(7);
        var sut = new DistributedPermissionCache(cacheMock.Object, counter.Object, Options.Create(DefaultOptions));

        await sut.InvalidateRoleAsync("editor");

        cacheMock.Verify(c => c.RemoveAsync("perm-role:v7:editor", It.IsAny<CancellationToken>()), Times.Once);
    }
}
