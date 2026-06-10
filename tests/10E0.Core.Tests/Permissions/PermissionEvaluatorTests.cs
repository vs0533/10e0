using Microsoft.Extensions.Options;
using TenE0.Core.Abstractions;
using TenE0.Core.Permissions;

namespace TenE0.Core.Tests.Permissions;

public sealed class PermissionEvaluatorTests
{
    private static Mock<ICurrentUserContext> CreateMockUser(bool isAuthenticated = true, string[]? roleIds = null)
    {
        var mock = new Mock<ICurrentUserContext>();
        mock.Setup(u => u.IsAuthenticated).Returns(isAuthenticated);
        mock.Setup(u => u.RoleIds).Returns(roleIds ?? Array.Empty<string>());
        // #7: 旧测试模拟 legacy token — 无 role_versions claim → 走兼容路径放行
        mock.Setup(u => u.RoleVersions).Returns(new Dictionary<string, long>());
        return mock;
    }

    [Fact]
    public async Task HasAsync_NotAuthenticated_ShouldReturnFalse()
    {
        var mockUser = CreateMockUser(isAuthenticated: false);
        var mockCache = new Mock<IPermissionCache>();
        var mockStore = new Mock<IPermissionStore>();
        var mockOptions = new Mock<IOptions<PermissionsOptions>>();
        mockOptions.Setup(o => o.Value).Returns(new PermissionsOptions());

        var sut = new PermissionEvaluator(mockUser.Object, mockStore.Object, mockCache.Object, Mock.Of<IRoleVersionStore>(), mockOptions.Object);

        var result = await sut.HasAsync("demo.view");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasAsync_SuperUser_ShouldShortCircuitTrue()
    {
        var mockUser = CreateMockUser(roleIds: new[] { "super_admin" });
        var mockCache = new Mock<IPermissionCache>();
        var mockStore = new Mock<IPermissionStore>();
        var mockOptions = new Mock<IOptions<PermissionsOptions>>();
        mockOptions.Setup(o => o.Value)
            .Returns(new PermissionsOptions { SuperUserRoles = new HashSet<string> { "super_admin" } });

        var sut = new PermissionEvaluator(mockUser.Object, mockStore.Object, mockCache.Object, Mock.Of<IRoleVersionStore>(), mockOptions.Object);

        var result = await sut.HasAsync("any.key");

        result.Should().BeTrue();
        mockCache.VerifyNoOtherCalls();
        mockStore.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HasAsync_CacheHit_ShouldUseCache()
    {
        var mockUser = CreateMockUser(roleIds: new[] { "editor" });
        var mockCache = new Mock<IPermissionCache>();
        mockCache.Setup(c => c.GetRolePermissionsAsync("editor", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "demo.view", "demo.update" });

        var mockStore = new Mock<IPermissionStore>();
        var mockOptions = new Mock<IOptions<PermissionsOptions>>();
        mockOptions.Setup(o => o.Value).Returns(new PermissionsOptions());

        var sut = new PermissionEvaluator(mockUser.Object, mockStore.Object, mockCache.Object, Mock.Of<IRoleVersionStore>(), mockOptions.Object);

        var result = await sut.HasAsync("demo.view");

        result.Should().BeTrue();
        mockStore.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HasAsync_CacheMiss_ShouldFallbackToStore()
    {
        var mockUser = CreateMockUser(roleIds: new[] { "editor" });
        var mockCache = new Mock<IPermissionCache>();
        mockCache.Setup(c => c.GetRolePermissionsAsync("editor", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<string>?)null);

        var mockStore = new Mock<IPermissionStore>();
        mockStore.Setup(s => s.GetGrantedPermissionsAsync(It.Is<IReadOnlyCollection<string>>(c => c.Contains("editor")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "demo.create" });

        var mockOptions = new Mock<IOptions<PermissionsOptions>>();
        mockOptions.Setup(o => o.Value).Returns(new PermissionsOptions());

        var sut = new PermissionEvaluator(mockUser.Object, mockStore.Object, mockCache.Object, Mock.Of<IRoleVersionStore>(), mockOptions.Object);

        var result = await sut.HasAsync("demo.create");

        result.Should().BeTrue();
        mockCache.Verify(c => c.SetRolePermissionsAsync("editor", It.IsAny<IReadOnlySet<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HasAsync_Denied_ShouldReturnFalse()
    {
        var mockUser = CreateMockUser(roleIds: new[] { "viewer" });
        var mockCache = new Mock<IPermissionCache>();
        mockCache.Setup(c => c.GetRolePermissionsAsync("viewer", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "demo.view" });

        var mockStore = new Mock<IPermissionStore>();
        var mockOptions = new Mock<IOptions<PermissionsOptions>>();
        mockOptions.Setup(o => o.Value).Returns(new PermissionsOptions());

        var sut = new PermissionEvaluator(mockUser.Object, mockStore.Object, mockCache.Object, Mock.Of<IRoleVersionStore>(), mockOptions.Object);

        var result = await sut.HasAsync("demo.delete");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasAnyAsync_EmptyKeys_ShouldReturnTrue()
    {
        var mockUser = CreateMockUser(roleIds: new[] { "viewer" });
        var mockCache = new Mock<IPermissionCache>();
        var mockStore = new Mock<IPermissionStore>();
        var mockOptions = new Mock<IOptions<PermissionsOptions>>();
        mockOptions.Setup(o => o.Value).Returns(new PermissionsOptions());

        var sut = new PermissionEvaluator(mockUser.Object, mockStore.Object, mockCache.Object, Mock.Of<IRoleVersionStore>(), mockOptions.Object);

        var result = await sut.HasAnyAsync(Array.Empty<string>());

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasAnyAsync_OneMatch_ShouldReturnTrue()
    {
        var mockUser = CreateMockUser(roleIds: new[] { "editor" });
        var mockCache = new Mock<IPermissionCache>();
        mockCache.Setup(c => c.GetRolePermissionsAsync("editor", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "demo.view", "demo.update" });

        var mockStore = new Mock<IPermissionStore>();
        var mockOptions = new Mock<IOptions<PermissionsOptions>>();
        mockOptions.Setup(o => o.Value).Returns(new PermissionsOptions());

        var sut = new PermissionEvaluator(mockUser.Object, mockStore.Object, mockCache.Object, Mock.Of<IRoleVersionStore>(), mockOptions.Object);

        var result = await sut.HasAnyAsync(new[] { "demo.update", "demo.delete" });

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasAnyAsync_NoMatch_ShouldReturnFalse()
    {
        var mockUser = CreateMockUser(roleIds: new[] { "viewer" });
        var mockCache = new Mock<IPermissionCache>();
        mockCache.Setup(c => c.GetRolePermissionsAsync("viewer", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "demo.view" });

        var mockStore = new Mock<IPermissionStore>();
        var mockOptions = new Mock<IOptions<PermissionsOptions>>();
        mockOptions.Setup(o => o.Value).Returns(new PermissionsOptions());

        var sut = new PermissionEvaluator(mockUser.Object, mockStore.Object, mockCache.Object, Mock.Of<IRoleVersionStore>(), mockOptions.Object);

        var result = await sut.HasAnyAsync(new[] { "demo.create", "demo.delete" });

        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasAllAsync_AllMatch_ShouldReturnTrue()
    {
        var mockUser = CreateMockUser(roleIds: new[] { "admin" });
        var mockCache = new Mock<IPermissionCache>();
        mockCache.Setup(c => c.GetRolePermissionsAsync("admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "demo.view", "demo.create", "demo.delete" });

        var mockStore = new Mock<IPermissionStore>();
        var mockOptions = new Mock<IOptions<PermissionsOptions>>();
        mockOptions.Setup(o => o.Value).Returns(new PermissionsOptions());

        var sut = new PermissionEvaluator(mockUser.Object, mockStore.Object, mockCache.Object, Mock.Of<IRoleVersionStore>(), mockOptions.Object);

        var result = await sut.HasAllAsync(new[] { "demo.view", "demo.create" });

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasAllAsync_OneMissing_ShouldReturnFalse()
    {
        var mockUser = CreateMockUser(roleIds: new[] { "editor" });
        var mockCache = new Mock<IPermissionCache>();
        mockCache.Setup(c => c.GetRolePermissionsAsync("editor", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<string>?)null);

        var mockStore = new Mock<IPermissionStore>();
        mockStore.Setup(s => s.GetGrantedPermissionsAsync(It.Is<IReadOnlyCollection<string>>(c => c.Contains("editor")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "demo.create" });

        var mockOptions = new Mock<IOptions<PermissionsOptions>>();
        mockOptions.Setup(o => o.Value).Returns(new PermissionsOptions());

        var sut = new PermissionEvaluator(mockUser.Object, mockStore.Object, mockCache.Object, Mock.Of<IRoleVersionStore>(), mockOptions.Object);

        var result = await sut.HasAllAsync(new[] { "demo.create", "demo.delete" });

        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasAsync_MultiRole_ShouldUnionPermissions()
    {
        var mockUser = CreateMockUser(roleIds: new[] { "editor", "viewer" });

        var mockCache = new Mock<IPermissionCache>();
        mockCache.Setup(c => c.GetRolePermissionsAsync("editor", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<string>?)null);
        mockCache.Setup(c => c.GetRolePermissionsAsync("viewer", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "demo.view" });

        var mockStore = new Mock<IPermissionStore>();
        mockStore.Setup(s => s.GetGrantedPermissionsAsync(It.Is<IReadOnlyCollection<string>>(c => c.Contains("editor")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "demo.update" });

        var mockOptions = new Mock<IOptions<PermissionsOptions>>();
        mockOptions.Setup(o => o.Value).Returns(new PermissionsOptions());

        var sut = new PermissionEvaluator(mockUser.Object, mockStore.Object, mockCache.Object, Mock.Of<IRoleVersionStore>(), mockOptions.Object);

        var hasView = await sut.HasAsync("demo.view");
        var hasUpdate = await sut.HasAsync("demo.update");

        hasView.Should().BeTrue("user should have view from the viewer role cache");
        hasUpdate.Should().BeTrue("user should have update from the editor role store fallback");
    }

    [Fact]
    public async Task HasAsync_SuperUser_HasAnyAsync_ShouldReturnTrue()
    {
        var mockUser = CreateMockUser(roleIds: new[] { "super_admin" });
        var mockCache = new Mock<IPermissionCache>();
        var mockStore = new Mock<IPermissionStore>();
        var mockOptions = new Mock<IOptions<PermissionsOptions>>();
        mockOptions.Setup(o => o.Value)
            .Returns(new PermissionsOptions { SuperUserRoles = new HashSet<string> { "super_admin" } });

        var sut = new PermissionEvaluator(mockUser.Object, mockStore.Object, mockCache.Object, Mock.Of<IRoleVersionStore>(), mockOptions.Object);

        var result = await sut.HasAnyAsync(new[] { "nonexistent.key" });

        result.Should().BeTrue("super user should bypass all permission checks");
        mockCache.VerifyNoOtherCalls();
        mockStore.VerifyNoOtherCalls();
    }
}
