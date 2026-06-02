using TenE0.Core.Permissions;

namespace TenE0.Core.Tests.Permissions;

public sealed class PermissionCatalogTests
{
    private sealed class TestProvider : IPermissionProvider
    {
        public IEnumerable<PermissionDefinition> Define() =>
        [
            new("demo.view", "查看演示", "Demo"),
            new("demo.create", "创建演示", "Demo"),
            new("demo.delete", "删除演示", "Demo"),
            new("user.view", "查看用户", "User"),
            new("ungrouped.key", "未分组权限"),
        ];
    }

    [Fact]
    public void Constructor_NoProviders_ShouldBeEmpty()
    {
        var sut = new PermissionCatalog(Array.Empty<IPermissionProvider>());

        sut.All.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_SingleProvider_ShouldLoad()
    {
        var sut = new PermissionCatalog([new TestProvider()]);

        sut.All.Should().HaveCount(5);
    }

    [Fact]
    public void Constructor_DuplicateKeys_ShouldUseFirst()
    {
        var provider1 = new Mock<IPermissionProvider>();
        provider1.Setup(p => p.Define())
            .Returns(new[] { new PermissionDefinition("demo.view", "Provider 1 定义", "Demo") });

        var provider2 = new Mock<IPermissionProvider>();
        provider2.Setup(p => p.Define())
            .Returns(new[] { new PermissionDefinition("demo.view", "Provider 2 定义", "Demo") });

        var sut = new PermissionCatalog([provider1.Object, provider2.Object]);

        sut.All.Should().HaveCount(1);
        sut.Find("demo.view")!.DisplayName.Should().Be("Provider 1 定义");
    }

    [Fact]
    public void Find_Existing_ShouldReturnDefinition()
    {
        var sut = new PermissionCatalog([new TestProvider()]);

        var result = sut.Find("demo.view");

        result.Should().NotBeNull();
        result!.Key.Should().Be("demo.view");
        result.DisplayName.Should().Be("查看演示");
        result.Group.Should().Be("Demo");
    }

    [Fact]
    public void Find_NonExisting_ShouldReturnNull()
    {
        var sut = new PermissionCatalog([new TestProvider()]);

        var result = sut.Find("nonexistent.key");

        result.Should().BeNull();
    }

    [Fact]
    public void Contains_Existing_ShouldBeTrue()
    {
        var sut = new PermissionCatalog([new TestProvider()]);

        sut.Contains("demo.view").Should().BeTrue();
    }

    [Fact]
    public void Contains_NonExisting_ShouldBeFalse()
    {
        var sut = new PermissionCatalog([new TestProvider()]);

        sut.Contains("nonexistent.key").Should().BeFalse();
    }

    [Fact]
    public void ByGroup_ShouldGroupCorrectly()
    {
        var sut = new PermissionCatalog([new TestProvider()]);

        var groups = sut.ByGroup;

        groups.Should().ContainKeys("Demo", "User", "default");
        groups["Demo"].Should().HaveCount(3);
        groups["User"].Should().HaveCount(1);
        groups["default"].Should().HaveCount(1);
        groups["default"].First().Key.Should().Be("ungrouped.key");
    }

    [Fact]
    public void ByGroup_NullGroup_ShouldUseDefault()
    {
        var provider = new Mock<IPermissionProvider>();
        provider.Setup(p => p.Define())
            .Returns(new[]
            {
                new PermissionDefinition("orphan.key", "孤立权限", null),
            });

        var sut = new PermissionCatalog([provider.Object]);

        var groups = sut.ByGroup;

        groups.Should().ContainKey("default");
        groups["default"].Should().ContainSingle()
            .Which.Key.Should().Be("orphan.key");
    }
}
