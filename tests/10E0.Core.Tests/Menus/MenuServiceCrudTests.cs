using TenE0.Core.Abstractions;
using TenE0.Core.Menus;
using TenE0Menu = TenE0.Core.Menus.Storage.TenE0Menu;
using StorageMenuType = TenE0.Core.Menus.Storage.MenuType;

namespace TenE0.Core.Tests.Menus;

[Trait("Category", "Unit")]
public sealed class MenuServiceCrudTests
{
    // ──────────────────────────────────────────────
    // Test DbContext
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
                entity.Property(e => e.TreePath).IsRequired(false);
            });
        }
    }

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private static TenE0Menu CreateMenu(
        string id,
        string name,
        string? parentId = null,
        int order = 0,
        int level = 0,
        string? treePath = null,
        StorageMenuType menuType = StorageMenuType.Menu,
        bool isActive = true)
    {
        return new TenE0Menu
        {
            Id = id,
            Name = name,
            RoutePath = $"/{name.ToLower()}",
            ParentId = parentId,
            Order = order,
            Level = level,
            TreePath = treePath ?? (parentId is null ? $"/{id}/" : ""),
            MenuType = menuType,
            IsActive = isActive,
            Layout = true,
        };
    }

    private sealed class TestDbContextFactory(DbContextOptions<TestDbContext> options)
        : IDbContextFactory<TestDbContext>
    {
        public TestDbContext CreateDbContext() => new(options);
    }

    private static IDbContextFactory<TestDbContext> CreateFactory(DbContextOptions<TestDbContext> options)
        => new TestDbContextFactory(options);

    private static MenuService<TestDbContext> CreateService(
        DbContextOptions<TestDbContext> options,
        Action<Mock<ICurrentUserContext>>? configureMock = null)
    {
        var currentUserMock = new Mock<ICurrentUserContext>();
        currentUserMock.SetupGet(c => c.IsAuthenticated).Returns(true);
        currentUserMock.SetupGet(c => c.UserCode).Returns("test-user");
        currentUserMock.SetupGet(c => c.RoleIds).Returns(new List<string> { "admin" });
        configureMock?.Invoke(currentUserMock);

        return new MenuService<TestDbContext>(CreateFactory(options), currentUserMock.Object);
    }

    // ──────────────────────────────────────────────
    // AddAsync Tests
    // ──────────────────────────────────────────────

    [Fact]
    public async Task AddAsync_RootNode_SetsLevelAndTreePathCorrectly()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;
        var service = CreateService(options);

        var request = new MenuCreateRequest(
            "Dashboard", "/dashboard", null, null, null, null, true, 1, MenuType.Menu);
        var result = await service.AddAsync(request);

        result.ParentId.Should().BeNull();
        result.Level.Should().Be(0);
        result.TreePath.Should().Be($"/{result.Id}/");
    }

    [Fact]
    public async Task AddAsync_ChildNode_CalculatesLevelAndTreePathFromParent()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        // Seed parent
        {
            await using var db = new TestDbContext(options);
            db.Menus.Add(CreateMenu("parent-1", "Parent", treePath: "/parent-1/", level: 0));
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        var request = new MenuCreateRequest(
            "Child", "/parent/child", "parent-1", null, null, null, true, 1, MenuType.Menu);
        var result = await service.AddAsync(request);

        result.ParentId.Should().Be("parent-1");
        result.Level.Should().Be(1);
        result.TreePath.Should().Be($"/parent-1/{result.Id}/");
    }

    [Fact]
    public async Task AddAsync_ParentNotFound_ThrowsInvalidOperationException()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;
        var service = CreateService(options);

        var request = new MenuCreateRequest(
            "Child", "/child", "nonexistent", null, null, null, true, 1, MenuType.Menu);

        var act = () => service.AddAsync(request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*父菜单不存在*");
    }

    [Fact]
    public async Task AddAsync_AllRequestFields_MapsCorrectly()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;
        var service = CreateService(options);

        // DTO MenuType.Directory(0) → StorageMenuType.Menu(0) via direct numeric cast
        var request = new MenuCreateRequest(
            "Settings", "/settings", null, "icon-gear", "views/settings/index",
            "/settings/profile", false, 5, MenuType.Directory);
        var result = await service.AddAsync(request);

        result.Name.Should().Be("Settings");
        result.RoutePath.Should().Be("/settings");
        result.ParentId.Should().BeNull();
        result.Icon.Should().Be("icon-gear");
        result.Component.Should().Be("views/settings/index");
        result.Redirect.Should().Be("/settings/profile");
        result.Layout.Should().BeFalse();
        result.Order.Should().Be(5);
        // DTO MenuType.Directory(0) → StorageMenuType.Menu(0)
        result.MenuType.Should().Be(StorageMenuType.Menu);
    }

    [Fact]
    public async Task AddAsync_MinimalRequest_SucceedsWithDefaults()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;
        var service = CreateService(options);

        var request = new MenuCreateRequest(
            "Minimal", "/minimal", null, null, null, null, true, 0, MenuType.Menu);

        var result = await service.AddAsync(request);

        result.Name.Should().Be("Minimal");
        result.Icon.Should().BeNull();
        result.Component.Should().BeNull();
        result.Redirect.Should().BeNull();
        result.Layout.Should().BeTrue();
    }

    [Fact]
    public async Task AddAsync_TreePathComputedAfterSave_HasCorrectValue()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        // Seed parent
        {
            await using var db = new TestDbContext(options);
            db.Menus.Add(CreateMenu("parent-x", "ParentX", treePath: "/parent-x/", level: 0));
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        var request = new MenuCreateRequest(
            "SubItem", "/parent-x/sub", "parent-x", null, null, null, true, 0, MenuType.Menu);
        var result = await service.AddAsync(request);

        // Verify by re-reading from DB
        {
            await using var db = new TestDbContext(options);
            var saved = await db.Menus.FindAsync(result.Id);
            saved.Should().NotBeNull();
            saved!.TreePath.Should().Be($"/parent-x/{result.Id}/");
        }
    }

    // ──────────────────────────────────────────────
    // UpdateAsync Tests
    // ──────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_FullUpdate_AllFieldsChanged()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.Menus.Add(CreateMenu("u1", "Original", menuType: StorageMenuType.Menu));
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        var request = new MenuUpdateRequest(
            "Updated", "/updated", "icon-new", "comp/new", "/new-redirect",
            false, 99, MenuType.Directory, false);

        await service.UpdateAsync("u1", request);

        {
            await using var db = new TestDbContext(options);
            var menu = await db.Menus.FindAsync("u1");
            menu.Should().NotBeNull();
            menu!.Name.Should().Be("Updated");
            menu.RoutePath.Should().Be("/updated");
            menu.Icon.Should().Be("icon-new");
            menu.Component.Should().Be("comp/new");
            menu.Redirect.Should().Be("/new-redirect");
            menu.Layout.Should().BeFalse();
            menu.Order.Should().Be(99);
            // DTO MenuType.Directory(0) → StorageMenuType.Menu(0)
            menu.MenuType.Should().Be(StorageMenuType.Menu);
            menu.IsActive.Should().BeFalse();
        }
    }

    [Fact]
    public async Task UpdateAsync_PartialUpdate_OnlyProvidedFieldsChanged()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.Menus.Add(CreateMenu("u2", "Original",
                menuType: StorageMenuType.Directory, treePath: "/u2/"));
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        var request = new MenuUpdateRequest(
            "JustName", null, null, null, null, null, null, null, null);

        await service.UpdateAsync("u2", request);

        {
            await using var db = new TestDbContext(options);
            var menu = await db.Menus.FindAsync("u2");
            menu.Should().NotBeNull();
            menu!.Name.Should().Be("JustName");
            menu.RoutePath.Should().Be("/original"); // unchanged
            menu.Icon.Should().BeNull(); // unchanged
            menu.Layout.Should().BeTrue(); // unchanged default
            menu.Order.Should().Be(0); // unchanged
            menu.MenuType.Should().Be(StorageMenuType.Directory); // unchanged
            menu.IsActive.Should().BeTrue(); // unchanged default
        }
    }

    [Fact]
    public async Task UpdateAsync_MenuNotFound_ThrowsInvalidOperationException()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;
        var service = CreateService(options);

        var request = new MenuUpdateRequest("Name", null, null, null, null, null, null, null, null);

        var act = () => service.UpdateAsync("nonexistent", request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*菜单不存在*");
    }

    [Fact]
    public async Task UpdateAsync_ToggleIsActive_ChangesFromTrueToFalse()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.Menus.Add(CreateMenu("u3", "Active", isActive: true));
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        var request = new MenuUpdateRequest(
            null, null, null, null, null, null, null, null, false);

        await service.UpdateAsync("u3", request);

        {
            await using var db = new TestDbContext(options);
            var menu = await db.Menus.FindAsync("u3");
            menu!.IsActive.Should().BeFalse();
        }
    }

    [Fact]
    public async Task UpdateAsync_NullOptionalFields_DoesNotChangeExistingValues()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            var menu = CreateMenu("u4", "WithIcon", menuType: StorageMenuType.Directory);
            menu.Icon = "old-icon";
            menu.Component = "old-component";
            db.Menus.Add(menu);
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        // Only set Name, leave Icon/Component as null (should not clear them)
        var request = new MenuUpdateRequest(
            "Renamed", "/renamed", null, null, null, null, null, null, null);

        await service.UpdateAsync("u4", request);

        {
            await using var db = new TestDbContext(options);
            var menu = await db.Menus.FindAsync("u4");
            menu!.Name.Should().Be("Renamed");
            menu.RoutePath.Should().Be("/renamed");
            menu.Icon.Should().Be("old-icon"); // preserved
            menu.Component.Should().Be("old-component"); // preserved
        }
    }

    [Fact]
    public async Task UpdateAsync_MenuTypeChange_ChangesFromMenuToDirectory()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.Menus.Add(CreateMenu("u5", "Item", menuType: StorageMenuType.Menu));
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        // DTO MenuType.Menu(1) → StorageMenuType.Directory(1)
        var request = new MenuUpdateRequest(
            null, null, null, null, null, null, null, MenuType.Menu, null);

        await service.UpdateAsync("u5", request);

        {
            await using var db = new TestDbContext(options);
            var menu = await db.Menus.FindAsync("u5");
            menu!.MenuType.Should().Be(StorageMenuType.Directory);
        }
    }

    // ──────────────────────────────────────────────
    // DeleteAsync Tests
    // ──────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_SoftDelete_SetsAuditFields()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.Menus.Add(CreateMenu("d1", "ToDelete"));
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        await service.DeleteAsync("d1");

        {
            await using var db = new TestDbContext(options);
            var menu = await db.Menus.FindAsync("d1");
            menu.Should().NotBeNull();
            menu!.IsSoftDelete.Should().BeTrue();
            menu.DeleteTime.Should().NotBeNull();
            menu.DeleteBy.Should().Be("test-user");
        }
    }

    [Fact]
    public async Task DeleteAsync_MenuNotFound_ThrowsInvalidOperationException()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;
        var service = CreateService(options);

        var act = () => service.DeleteAsync("nonexistent");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*菜单不存在*");
    }

    [Fact]
    public async Task DeleteAsync_AlreadyDeleted_DoesNotThrow()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            var menu = CreateMenu("d2", "AlreadyDeleted");
            menu.IsSoftDelete = true;
            menu.DeleteTime = DateTimeOffset.UtcNow.AddDays(-1);
            db.Menus.Add(menu);
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);

        // Should not throw — DeleteAsync doesn't check IsSoftDelete
        await service.DeleteAsync("d2");

        {
            await using var db = new TestDbContext(options);
            var menu = await db.Menus.FindAsync("d2");
            menu!.IsSoftDelete.Should().BeTrue();
            menu.DeleteBy.Should().Be("test-user");
        }
    }

    [Fact]
    public async Task DeleteAsync_AuditFields_SetCorrectly()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.Menus.Add(CreateMenu("d3", "AuditCheck"));
            await db.SaveChangesAsync();
        }

        var before = DateTimeOffset.UtcNow;
        var service = CreateService(options);
        await service.DeleteAsync("d3");

        {
            await using var db = new TestDbContext(options);
            var menu = await db.Menus.FindAsync("d3");
            menu!.IsSoftDelete.Should().BeTrue();
            menu.DeleteTime.Should().BeOnOrAfter(before);
            menu.DeleteTime.Should().BeOnOrBefore(DateTimeOffset.UtcNow);
            menu.DeleteBy.Should().Be("test-user");
        }
    }

    // ──────────────────────────────────────────────
    // MoveAsync Tests
    // ──────────────────────────────────────────────

    [Fact]
    public async Task MoveAsync_ToRoot_SetsParentIdLevelAndTreePath()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.Menus.Add(CreateMenu("root", "Root", treePath: "/root/", level: 0));
            db.Menus.Add(CreateMenu("m1", "Child", parentId: "root", level: 1, treePath: "/root/m1/"));
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        await service.MoveAsync("m1", null);

        {
            await using var db = new TestDbContext(options);
            var menu = await db.Menus.FindAsync("m1");
            menu!.ParentId.Should().BeNull();
            menu.Level.Should().Be(0);
            menu.TreePath.Should().Be("/m1/");
        }
    }

    [Fact]
    public async Task MoveAsync_ToNewParent_RecalculatesPathAndLevel()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.Menus.Add(CreateMenu("root", "Root", treePath: "/root/", level: 0));
            db.Menus.Add(CreateMenu("m2", "Child", parentId: "root", level: 1, treePath: "/root/m2/"));
            db.Menus.Add(CreateMenu("new-parent", "NewParent", treePath: "/new-parent/", level: 0));
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        await service.MoveAsync("m2", "new-parent");

        {
            await using var db = new TestDbContext(options);
            var menu = await db.Menus.FindAsync("m2");
            menu!.ParentId.Should().Be("new-parent");
            menu.Level.Should().Be(1);
            menu.TreePath.Should().Be("/new-parent/m2/");
        }
    }

    [Fact]
    public async Task MoveAsync_ToSelf_ThrowsInvalidOperationException()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.Menus.Add(CreateMenu("m3", "Self", treePath: "/m3/", level: 0));
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        var act = () => service.MoveAsync("m3", "m3");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*不能移动到自身*");
    }

    [Fact]
    public async Task MoveAsync_ToDescendant_ThrowsInvalidOperationException()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.Menus.Add(CreateMenu("m4", "Parent", treePath: "/m4/", level: 0));
            db.Menus.Add(CreateMenu("m4-child", "Child", parentId: "m4",
                level: 1, treePath: "/m4/m4-child/"));
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        // Move parent to its own child — descendant
        var act = () => service.MoveAsync("m4", "m4-child");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*不能移动到自己的后代节点*");
    }

    [Fact]
    public async Task MoveAsync_TargetParentNotFound_ThrowsInvalidOperationException()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.Menus.Add(CreateMenu("m5", "Orphan", treePath: "/m5/", level: 0));
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        var act = () => service.MoveAsync("m5", "nonexistent");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*目标父菜单不存在*");
    }

    [Fact]
    public async Task MoveAsync_MenuNotFound_ThrowsInvalidOperationException()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;
        var service = CreateService(options);

        var act = () => service.MoveAsync("nonexistent", null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*菜单不存在*");
    }

    [Fact]
    public async Task MoveAsync_SubtreeRecalculation_UpdatesAllDescendantsTreePaths()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.Menus.Add(CreateMenu("r1", "Root1", treePath: "/r1/", level: 0));
            db.Menus.Add(CreateMenu("c1", "Child1", parentId: "r1", level: 1, treePath: "/r1/c1/"));
            db.Menus.Add(CreateMenu("gc1", "Grandchild1", parentId: "c1", level: 2, treePath: "/r1/c1/gc1/"));
            db.Menus.Add(CreateMenu("r2", "Root2", treePath: "/r2/", level: 0));
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        // Move "c1" (with its subtree) to root (newParentId=null)
        await service.MoveAsync("c1", null);

        {
            await using var db = new TestDbContext(options);
            var child = await db.Menus.FindAsync("c1");
            child!.TreePath.Should().Be("/c1/");
            child.Level.Should().Be(0);

            var grandchild = await db.Menus.FindAsync("gc1");
            grandchild!.TreePath.Should().Be("/c1/gc1/");
            grandchild.Level.Should().Be(1);
        }
    }

    [Fact]
    public async Task MoveAsync_SubtreeLevelRecalculation_AdjustsDescendantsLevel()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.Menus.Add(CreateMenu("rA", "RootA", treePath: "/rA/", level: 0));
            db.Menus.Add(CreateMenu("cA", "ChildA", parentId: "rA",
                level: 1, treePath: "/rA/cA/"));
            db.Menus.Add(CreateMenu("gcA", "GrandA", parentId: "cA",
                level: 2, treePath: "/rA/cA/gcA/"));
            db.Menus.Add(CreateMenu("rB", "RootB", treePath: "/rB/", level: 0));
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        // Move "cA" (Level=1) to "rB" (Level=0) — levelDiff = 1-1 = 0
        // So only TreePath changes, not Level
        await service.MoveAsync("cA", "rB");

        {
            await using var db = new TestDbContext(options);
            var child = await db.Menus.FindAsync("cA");
            child!.TreePath.Should().Be("/rB/cA/");
            child.Level.Should().Be(1);

            var grand = await db.Menus.FindAsync("gcA");
            grand!.TreePath.Should().Be("/rB/cA/gcA/");
            grand.Level.Should().Be(2);
        }
    }

    [Fact]
    public async Task MoveAsync_BetweenBranches_OldParentExcludesMovedNode()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.Menus.Add(CreateMenu("src", "Source", treePath: "/src/", level: 0));
            db.Menus.Add(CreateMenu("mid", "Middle", parentId: "src",
                level: 1, treePath: "/src/mid/"));
            db.Menus.Add(CreateMenu("dst", "Dest", treePath: "/dst/", level: 0));
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        await service.MoveAsync("mid", "dst");

        {
            await using var db = new TestDbContext(options);

            // Old parent no longer has mid as child
            var oldParentChildren = await db.Menus
                .Where(m => m.ParentId == "src")
                .ToListAsync();
            oldParentChildren.Should().BeEmpty();

            // New parent now has mid as child
            var newParentChildren = await db.Menus
                .Where(m => m.ParentId == "dst")
                .ToListAsync();
            newParentChildren.Should().ContainSingle(m => m.Id == "mid");
        }
    }
}
