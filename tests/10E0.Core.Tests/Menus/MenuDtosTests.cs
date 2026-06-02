using TenE0.Core.Menus;

namespace TenE0.Core.Tests.Menus;

public sealed class MenuTypeTests
{
    [Fact]
    public void MenuType_ShouldHaveAllExpectedValues()
    {
        var values = Enum.GetValues<MenuType>().ToList();

        values.Should().Contain(MenuType.Directory);
        values.Should().Contain(MenuType.Menu);
        values.Should().Contain(MenuType.Button);
        values.Should().HaveCount(3);
    }

    [Fact]
    public void MenuType_Values_ShouldHaveExpectedIntMapping()
    {
        ((int)MenuType.Directory).Should().Be(0);
        ((int)MenuType.Menu).Should().Be(1);
        ((int)MenuType.Button).Should().Be(2);
    }

    [Fact]
    public void MenuType_ParseFromInt_ShouldWork()
    {
        var dir = (MenuType)0;
        var menu = (MenuType)1;
        var button = (MenuType)2;

        dir.Should().Be(MenuType.Directory);
        menu.Should().Be(MenuType.Menu);
        button.Should().Be(MenuType.Button);
    }
}

public sealed class MenuDtosTests
{
    [Fact]
    public void MenuCreateRequest_Constructor_ShouldSetAllFields()
    {
        var req = new MenuCreateRequest(
            "Dashboard", "/dashboard", null, "icon-dashboard",
            "views/dashboard/index", "/dashboard/overview",
            true, 1, MenuType.Directory);

        req.Name.Should().Be("Dashboard");
        req.RoutePath.Should().Be("/dashboard");
        req.ParentId.Should().BeNull();
        req.Icon.Should().Be("icon-dashboard");
        req.Component.Should().Be("views/dashboard/index");
        req.Redirect.Should().Be("/dashboard/overview");
        req.Layout.Should().BeTrue();
        req.Order.Should().Be(1);
        req.MenuType.Should().Be(MenuType.Directory);
    }

    [Fact]
    public void MenuUpdateRequest_AllOptionalFields_ShouldBeNullByDefault()
    {
        var req = new MenuUpdateRequest(null, null, null, null, null, null, null, null, null);

        req.Name.Should().BeNull();
        req.IsActive.Should().BeNull();
        req.Order.Should().BeNull();
        req.MenuType.Should().BeNull();
    }

    [Fact]
    public void MenuUpdateRequest_PartialUpdate_ShouldPreserveNonNull()
    {
        var req = new MenuUpdateRequest("Updated Name", null, null, null, null, false, null, null, true);

        req.Name.Should().Be("Updated Name");
        req.Layout.Should().BeFalse();
        req.IsActive.Should().BeTrue();
        req.RoutePath.Should().BeNull();
    }

    [Fact]
    public void MenuTreeNode_DefaultValues_ShouldBeEmpty()
    {
        var node = new MenuTreeNode();

        node.Id.Should().Be("");
        node.Name.Should().Be("");
        node.RoutePath.Should().Be("");
        node.Children.Should().BeEmpty();
        node.IsActive.Should().BeFalse();
        node.Order.Should().Be(0);
    }

    [Fact]
    public void MenuTreeNode_CanBuildHierarchy()
    {
        var root = new MenuTreeNode
        {
            Id = "1",
            Name = "Root",
            MenuType = MenuType.Directory
        };

        var child = new MenuTreeNode
        {
            Id = "2",
            Name = "Child",
            MenuType = MenuType.Menu
        };

        root.Children.Add(child);

        root.Children.Should().HaveCount(1);
        root.Children[0].Id.Should().Be("2");
        root.Children[0].MenuType.Should().Be(MenuType.Menu);
    }
}
