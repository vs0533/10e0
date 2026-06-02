using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using TenE0.Core.Permissions;
using Moq;

namespace TenE0.Core.Tests.Permissions;

[Trait("Category", "Unit")]
public sealed class DistributedPermissionCacheTests
{
    private static PermissionsOptions DefaultOptions => new()
    {
        SuperUserRoles = new HashSet<string>(StringComparer.Ordinal),
        CacheDuration = TimeSpan.FromMinutes(5)
    };

    [Fact]
    public async Task GetRolePermissionsAsync_CacheHit_ReturnsDeserializedSet()
    {
        var cacheMock = new Mock<IDistributedCache>();
        var permissions = new HashSet<string> { "user.read", "user.write" };
        var json = JsonSerializer.Serialize(permissions);
        // BuildKeyAsync reads version first
        cacheMock.Setup(c => c.GetAsync("perm-cache:version", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Encoding.UTF8.GetBytes("0"));
        // Then reads the role permissions
        cacheMock.Setup(c => c.GetAsync("perm-role:v0:admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Encoding.UTF8.GetBytes(json));

        var sut = new DistributedPermissionCache(cacheMock.Object, Options.Create(DefaultOptions));
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

        var sut = new DistributedPermissionCache(cacheMock.Object, Options.Create(DefaultOptions));
        var result = await sut.GetRolePermissionsAsync("viewer");

        result.Should().BeNull();
    }

    [Fact]
    public async Task SetRolePermissionsAsync_SerializesAndStores()
    {
        var cacheMock = new Mock<IDistributedCache>();
        cacheMock.Setup(c => c.GetAsync("perm-cache:version", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Encoding.UTF8.GetBytes("0"));
        var sut = new DistributedPermissionCache(cacheMock.Object, Options.Create(DefaultOptions));

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
        cacheMock.Setup(c => c.GetAsync("perm-cache:version", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Encoding.UTF8.GetBytes("0"));
        var sut = new DistributedPermissionCache(cacheMock.Object, Options.Create(DefaultOptions));

        await sut.InvalidateRoleAsync("admin");

        cacheMock.Verify(c => c.RemoveAsync("perm-role:v0:admin", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InvalidateAllAsync_IncrementsVersion()
    {
        var cacheMock = new Mock<IDistributedCache>();
        cacheMock.Setup(c => c.GetAsync("perm-cache:version", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Encoding.UTF8.GetBytes("3"));
        var sut = new DistributedPermissionCache(cacheMock.Object, Options.Create(DefaultOptions));

        await sut.InvalidateAllAsync();

        cacheMock.Verify(
            c => c.SetAsync("perm-cache:version",
                It.Is<byte[]>(v => Encoding.UTF8.GetString(v) == "4"),
                It.IsAny<DistributedCacheEntryOptions>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InvalidateAllAsync_NoExistingVersion_StartsAt1()
    {
        var cacheMock = new Mock<IDistributedCache>();
        cacheMock.Setup(c => c.GetAsync("perm-cache:version", It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);
        var sut = new DistributedPermissionCache(cacheMock.Object, Options.Create(DefaultOptions));

        await sut.InvalidateAllAsync();

        cacheMock.Verify(
            c => c.SetAsync("perm-cache:version",
                It.Is<byte[]>(v => Encoding.UTF8.GetString(v) == "1"),
                It.IsAny<DistributedCacheEntryOptions>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
