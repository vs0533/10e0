using System.Reflection;
using TenE0.Core.Menus;
using TenE0Menu = TenE0.Core.Menus.Storage.TenE0Menu;
using StorageMenuType = TenE0.Core.Menus.Storage.MenuType;
using Microsoft.EntityFrameworkCore;

namespace TenE0.Core.Tests.Menus;

[Trait("Category", "Unit")]
public sealed class MenuServiceStaticTests
{
    // ──────────────────────────────────────────────
    // Test DbContext (concrete type for generic)
    // ──────────────────────────────────────────────

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TenE0Menu> Menus => Set<TenE0Menu>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TenE0Menu>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Ignore(e => e.Children);
            });
        }
    }

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private static TenE0Menu CreateMenu(
        string id,
        string name,
        string routePath,
        string? parentId = null,
        int order = 0,
        StorageMenuType menuType = StorageMenuType.Menu,
        string? component = null,
        bool isActive = true)
    {
        return new TenE0Menu
        {
            Id = id,
            Name = name,
            RoutePath = routePath,
            ParentId = parentId,
            Order = order,
            MenuType = menuType,
            Component = component,
            IsActive = isActive,
            Layout = true,
            TreePath = "",
            Level = 0,
        };
    }

    private static IReadOnlyList<MenuTreeNode> InvokeBuildTree(
        IReadOnlyList<TenE0Menu> menus, HashSet<string>? allowedIds)
    {
        var method = typeof(MenuService<TestDbContext>).GetMethod(
            "BuildTree", BindingFlags.NonPublic | BindingFlags.Static)!;

        return (IReadOnlyList<MenuTreeNode>)method.Invoke(null, [menus, allowedIds])!;
    }

    private static MenuTreeNode InvokeBuildNode(
        TenE0Menu m, Dictionary<string, List<TenE0Menu>> byParent)
    {
        var method = typeof(MenuService<TestDbContext>).GetMethod(
            "BuildNode", BindingFlags.NonPublic | BindingFlags.Static)!;

        return (MenuTreeNode)method.Invoke(null, [m, byParent])!;
    }

    // ──────────────────────────────────────────────
    // BuildTree Tests
    // ──────────────────────────────────────────────

    [Fact]
    public void BuildTree_SingleRoot_ReturnsOneNode()
    {
        var menus = new List<TenE0Menu>
        {
            CreateMenu("1", "Dashboard", "/dashboard"),
        };

        var result = InvokeBuildTree(menus, null);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("1");
        result[0].Name.Should().Be("Dashboard");
        result[0].RoutePath.Should().Be("/dashboard");
        result[0].Children.Should().BeEmpty();
    }

    [Fact]
    public void BuildTree_RootAndChild_RootHasChild()
    {
        var menus = new List<TenE0Menu>
        {
            CreateMenu("1", "Root", "/root"),
            CreateMenu("2", "Child", "/root/child", parentId: "1"),
        };

        var result = InvokeBuildTree(menus, null);

        result.Should().HaveCount(1);
        result[0].Children.Should().HaveCount(1);
        result[0].Children[0].Id.Should().Be("2");
        result[0].Children[0].Name.Should().Be("Child");
    }

    [Fact]
    public void BuildTree_MultipleRoots_ReturnsAllRoots()
    {
        var menus = new List<TenE0Menu>
        {
            CreateMenu("1", "Dashboard", "/dashboard"),
            CreateMenu("2", "Settings", "/settings"),
            CreateMenu("3", "Reports", "/reports"),
        };

        var result = InvokeBuildTree(menus, null);

        result.Should().HaveCount(3);
        result.Select(n => n.Id).Should().BeEquivalentTo(["1", "2", "3"], o => o.WithStrictOrdering());
    }

    [Fact]
    public void BuildTree_DeepNesting_MapsFullChain()
    {
        var menus = new List<TenE0Menu>
        {
            CreateMenu("1", "Root", "/root"),
            CreateMenu("2", "Child", "/root/child", parentId: "1"),
            CreateMenu("3", "Grandchild", "/root/child/grand", parentId: "2"),
        };

        var result = InvokeBuildTree(menus, null);

        result.Should().HaveCount(1);
        var root = result[0];
        root.Children.Should().HaveCount(1);
        var child = root.Children[0];
        child.Id.Should().Be("2");
        child.Children.Should().HaveCount(1);
        child.Children[0].Id.Should().Be("3");
        child.Children[0].Children.Should().BeEmpty();
    }

    [Fact]
    public void BuildTree_OrderPreserved_SortedByOrderWithinLevel()
    {
        var menus = new List<TenE0Menu>
        {
            CreateMenu("1", "Z-Last", "/last", order: 10),
            CreateMenu("2", "A-First", "/first", order: 0),
            CreateMenu("3", "M-Mid", "/mid", order: 5),
        };

        var result = InvokeBuildTree(menus, null);

        result.Should().HaveCount(3);
        result[0].Id.Should().Be("2"); // order 0
        result[1].Id.Should().Be("3"); // order 5
        result[2].Id.Should().Be("1"); // order 10
    }

    [Fact]
    public void BuildTree_AllowedIdsFilter_OnlyFilteredMenusAppear()
    {
        var menus = new List<TenE0Menu>
        {
            CreateMenu("1", "Root", "/root"),
            CreateMenu("2", "Child-A", "/root/a", parentId: "1"),
            CreateMenu("3", "Child-B", "/root/b", parentId: "1"),
        };
        var allowedIds = new HashSet<string> { "1", "2" };

        var result = InvokeBuildTree(menus, allowedIds);

        result.Should().HaveCount(1);
        result[0].Children.Should().HaveCount(1);
        result[0].Children[0].Id.Should().Be("2");
    }

    [Fact]
    public void BuildTree_AllowedIdsExcludesParent_ChildNotInTree()
    {
        var menus = new List<TenE0Menu>
        {
            CreateMenu("1", "Root", "/root"),
            CreateMenu("2", "Child", "/root/child", parentId: "1"),
        };
        var allowedIds = new HashSet<string> { "2" };

        var result = InvokeBuildTree(menus, allowedIds);

        result.Should().BeEmpty();
    }

    [Fact]
    public void BuildTree_NoAllowedIds_AllMenusReturned()
    {
        var menus = new List<TenE0Menu>
        {
            CreateMenu("1", "Root", "/root"),
            CreateMenu("2", "Child", "/root/child", parentId: "1"),
        };

        var result = InvokeBuildTree(menus, null);

        result.Should().HaveCount(1);
        result[0].Children.Should().HaveCount(1);
    }

    [Fact]
    public void BuildTree_EmptyMenuList_ReturnsEmpty()
    {
        var menus = new List<TenE0Menu>();

        var result = InvokeBuildTree(menus, null);

        result.Should().BeEmpty();
    }

    // ──────────────────────────────────────────────
    // BuildNode Tests
    // ──────────────────────────────────────────────

    [Fact]
    public void BuildNode_WithoutChildren_ChildrenIsEmptyList()
    {
        var menu = CreateMenu("1", "Leaf", "/leaf");
        var byParent = new Dictionary<string, List<TenE0Menu>>();

        var result = InvokeBuildNode(menu, byParent);

        result.Children.Should().NotBeNull();
        result.Children.Should().BeEmpty();
    }

    [Fact]
    public void BuildNode_WithChildren_ChildrenPopulated()
    {
        var child = CreateMenu("2", "Child", "/parent/child", parentId: "1");
        var menu = CreateMenu("1", "Parent", "/parent");

        var byParent = new Dictionary<string, List<TenE0Menu>>
        {
            ["1"] = [child],
        };

        var result = InvokeBuildNode(menu, byParent);

        result.Children.Should().HaveCount(1);
        result.Children[0].Id.Should().Be("2");
        result.Children[0].Name.Should().Be("Child");
    }

    [Fact]
    public void BuildNode_MapsAllFields_Correctly()
    {
        var menu = CreateMenu(
            id: "42",
            name: "User Management",
            routePath: "/users",
            menuType: StorageMenuType.Menu,
            component: "views/users/index",
            order: 5);

        // Set additional properties
        menu.Icon = "icon-users";
        menu.Redirect = "/users/list";
        menu.Layout = false;
        menu.IsActive = false;

        var byParent = new Dictionary<string, List<TenE0Menu>>();

        var result = InvokeBuildNode(menu, byParent);

        result.Id.Should().Be("42");
        result.Name.Should().Be("User Management");
        result.RoutePath.Should().Be("/users");
        result.Icon.Should().Be("icon-users");
        result.Component.Should().Be("views/users/index");
        result.Redirect.Should().Be("/users/list");
        result.Layout.Should().BeFalse();
        result.Order.Should().Be(5);
        result.IsActive.Should().BeFalse();
    }

    [Fact]
    public void BuildNode_MenuTypeDirectory_MappedCorrectly()
    {
        // Storage.MenuType.Directory (1) → Menus.MenuType.Menu (1)
        var menu = CreateMenu("1", "Directory", "/dir", menuType: StorageMenuType.Directory);
        var byParent = new Dictionary<string, List<TenE0Menu>>();

        var result = InvokeBuildNode(menu, byParent);

        result.MenuType.Should().Be(MenuType.Menu);
    }

    [Fact]
    public void BuildNode_MenuTypeButton_MappedCorrectly()
    {
        // Storage.MenuType.Button (2) → Menus.MenuType.Button (2)
        var menu = CreateMenu("1", "Button", "/btn", menuType: StorageMenuType.Button);
        var byParent = new Dictionary<string, List<TenE0Menu>>();

        var result = InvokeBuildNode(menu, byParent);

        result.MenuType.Should().Be(MenuType.Button);
    }

    [Fact]
    public void BuildNode_ComponentNull_MappedAsNull()
    {
        var menu = CreateMenu("1", "No Component", "/no-comp", component: null);
        var byParent = new Dictionary<string, List<TenE0Menu>>();

        var result = InvokeBuildNode(menu, byParent);

        result.Component.Should().BeNull();
    }

    [Fact]
    public void BuildNode_PreservesByParentOrder()
    {
        var childA = CreateMenu("2", "A-Child", "/parent/a", parentId: "1", order: 10);
        var childB = CreateMenu("3", "B-Child", "/parent/b", parentId: "1", order: 0);
        var menu = CreateMenu("1", "Parent", "/parent");

        var byParent = new Dictionary<string, List<TenE0Menu>>
        {
            ["1"] = [childA, childB], // BuildNode preserves the order from byParent
        };

        var result = InvokeBuildNode(menu, byParent);

        result.Children.Should().HaveCount(2);
        result.Children[0].Id.Should().Be("2");
        result.Children[1].Id.Should().Be("3");
    }
}
