using TenE0.Core.Abstractions;
using TenE0.Core.Menus;
using TenE0Menu = TenE0.Core.Menus.Storage.TenE0Menu;
using TenE0RoleMenu = TenE0.Core.Menus.Storage.TenE0RoleMenu;
using StorageMenuType = TenE0.Core.Menus.Storage.MenuType;

namespace TenE0.Core.Tests.Menus;

[Trait("Category", "Unit")]
public sealed class MenuServiceQueryTests
{
    // ──────────────────────────────────────────────
    // Test DbContext
    // ──────────────────────────────────────────────

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TenE0Menu> Menus => Set<TenE0Menu>();
        public DbSet<TenE0RoleMenu> RoleMenus => Set<TenE0RoleMenu>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TenE0Menu>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Ignore(e => e.Children);
                entity.Property(e => e.TreePath).IsRequired(false);
            });
            modelBuilder.Entity<TenE0RoleMenu>(entity =>
            {
                entity.HasKey(e => new { e.RoleCode, e.MenuId });
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
    // GetAllAsync Tests
    // ──────────────────────────────────────────────

    [Fact]
    public async Task GetAllAsync_ReturnsNonDeletedMenus()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.Menus.Add(CreateMenu("1", "Active"));
            var deleted = CreateMenu("2", "Deleted");
            deleted.IsSoftDelete = true;
            db.Menus.Add(deleted);
            db.Menus.Add(CreateMenu("3", "AlsoActive"));
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        var result = await service.GetAllAsync();

        result.Select(m => m.Id).Should().BeEquivalentTo(["1", "3"]);
    }

    [Fact]
    public async Task GetAllAsync_OrderedByOrder()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.Menus.Add(CreateMenu("1", "Z", order: 10));
            db.Menus.Add(CreateMenu("2", "A", order: 0));
            db.Menus.Add(CreateMenu("3", "M", order: 5));
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        var result = await service.GetAllAsync();

        result.Select(m => m.Id).Should().Equal(["2", "3", "1"]);
    }

    [Fact]
    public async Task GetAllAsync_EmptyDatabase_ReturnsEmpty()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;
        var service = CreateService(options);

        var result = await service.GetAllAsync();

        result.Should().BeEmpty();
    }

    // ──────────────────────────────────────────────
    // GetMenuTreeAsync Tests
    // ──────────────────────────────────────────────

    [Fact]
    public async Task GetMenuTreeAsync_FlatMenus_BuildsCorrectTree()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.Menus.Add(CreateMenu("1", "Root", treePath: "/1/", level: 0));
            db.Menus.Add(CreateMenu("2", "Child", parentId: "1", level: 1, treePath: "/1/2/"));
            db.Menus.Add(CreateMenu("3", "Other", treePath: "/3/", level: 0));
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        var result = await service.GetMenuTreeAsync();

        result.Should().HaveCount(2);
        result[0].Id.Should().Be("1");
        result[0].Children.Should().HaveCount(1);
        result[0].Children[0].Id.Should().Be("2");
        result[1].Id.Should().Be("3");
        result[1].Children.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMenuTreeAsync_OrderPreservedInTree()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.Menus.Add(CreateMenu("1", "B-Root", order: 10));
            db.Menus.Add(CreateMenu("2", "A-Root", order: 0));
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        var result = await service.GetMenuTreeAsync();

        result.Should().HaveCount(2);
        result[0].Id.Should().Be("2"); // order 0
        result[1].Id.Should().Be("1"); // order 10
    }

    // ──────────────────────────────────────────────
    // GetUserMenusAsync Tests
    // ──────────────────────────────────────────────

    [Fact]
    public async Task GetUserMenusAsync_NotAuthenticated_ReturnsEmpty()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        var service = CreateService(options, mock =>
        {
            mock.SetupGet(c => c.IsAuthenticated).Returns(false);
        });

        var result = await service.GetUserMenusAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUserMenusAsync_NoRoles_ReturnsEmpty()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        var service = CreateService(options, mock =>
        {
            mock.SetupGet(c => c.RoleIds).Returns(new List<string>());
        });

        var result = await service.GetUserMenusAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUserMenusAsync_ReturnsMenusMatchingUserRoles()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.Menus.Add(CreateMenu("1", "Dashboard"));
            db.Menus.Add(CreateMenu("2", "Settings"));
            db.Menus.Add(CreateMenu("3", "Reports"));
            db.RoleMenus.Add(new TenE0RoleMenu { RoleCode = "admin", MenuId = "1" });
            db.RoleMenus.Add(new TenE0RoleMenu { RoleCode = "admin", MenuId = "2" });
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        var result = await service.GetUserMenusAsync();

        result.Select(m => m.Id).Should().BeEquivalentTo(["1", "2"]);
    }

    [Fact]
    public async Task GetUserMenusAsync_OnlyActiveNonDeletedMenus()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.Menus.Add(CreateMenu("1", "Active"));
            var inactive = CreateMenu("2", "Inactive", isActive: false);
            db.Menus.Add(inactive);
            var deleted = CreateMenu("3", "Deleted");
            deleted.IsSoftDelete = true;
            db.Menus.Add(deleted);
            db.RoleMenus.Add(new TenE0RoleMenu { RoleCode = "admin", MenuId = "1" });
            db.RoleMenus.Add(new TenE0RoleMenu { RoleCode = "admin", MenuId = "2" });
            db.RoleMenus.Add(new TenE0RoleMenu { RoleCode = "admin", MenuId = "3" });
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        var result = await service.GetUserMenusAsync();

        result.Select(m => m.Id).Should().BeEquivalentTo(["1"]);
    }

    [Fact]
    public async Task GetUserMenusAsync_DistinctMenus_NoDuplicates()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.Menus.Add(CreateMenu("1", "Shared"));
            // Menu assigned to two roles the user has
            db.RoleMenus.Add(new TenE0RoleMenu { RoleCode = "admin", MenuId = "1" });
            db.RoleMenus.Add(new TenE0RoleMenu { RoleCode = "editor", MenuId = "1" });
            await db.SaveChangesAsync();
        }

        var service = CreateService(options, mock =>
        {
            mock.SetupGet(c => c.RoleIds).Returns(new List<string> { "admin", "editor" });
        });

        var result = await service.GetUserMenusAsync();

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("1");
    }

    // ──────────────────────────────────────────────
    // GetUserMenuTreeAsync Tests
    // ──────────────────────────────────────────────

    [Fact]
    public async Task GetUserMenuTreeAsync_AncestorsCollected()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            // Root → Child → Leaf (user only has Leaf assigned)
            db.Menus.Add(CreateMenu("root", "Root", treePath: "/root/", level: 0));
            db.Menus.Add(CreateMenu("child", "Child", parentId: "root",
                level: 1, treePath: "/root/child/"));
            db.Menus.Add(CreateMenu("leaf", "Leaf", parentId: "child",
                level: 2, treePath: "/root/child/leaf/"));
            db.RoleMenus.Add(new TenE0RoleMenu { RoleCode = "admin", MenuId = "leaf" });
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        var result = await service.GetUserMenuTreeAsync();

        // Tree should include root → child → leaf even though only leaf was assigned
        result.Should().HaveCount(1);
        result[0].Id.Should().Be("root");
        result[0].Children.Should().HaveCount(1);
        result[0].Children[0].Id.Should().Be("child");
        result[0].Children[0].Children.Should().HaveCount(1);
        result[0].Children[0].Children[0].Id.Should().Be("leaf");
    }

    [Fact]
    public async Task GetUserMenuTreeAsync_EmptyAllowedMenus_ReturnsEmpty()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.Menus.Add(CreateMenu("1", "Menu"));
            // No role assignments
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        var result = await service.GetUserMenuTreeAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUserMenuTreeAsync_ActiveNonDeletedCheck()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            var inactive = CreateMenu("1", "Inactive", isActive: false, treePath: "/1/", level: 0);
            db.Menus.Add(inactive);
            db.RoleMenus.Add(new TenE0RoleMenu { RoleCode = "admin", MenuId = "1" });

            var deleted = CreateMenu("2", "Deleted");
            deleted.IsSoftDelete = true;
            db.Menus.Add(deleted);
            db.RoleMenus.Add(new TenE0RoleMenu { RoleCode = "admin", MenuId = "2" });
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        var result = await service.GetUserMenuTreeAsync();

        result.Should().BeEmpty();
    }

    // ──────────────────────────────────────────────
    // AssignToRoleAsync Tests
    // ──────────────────────────────────────────────

    [Fact]
    public async Task AssignToRoleAsync_AddNewAssignments()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        // Existing assignment for role "admin": menu "1"
        {
            await using var db = new TestDbContext(options);
            db.RoleMenus.Add(new TenE0RoleMenu { RoleCode = "admin", MenuId = "1" });
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        // Add "2" and "3" while keeping "1"
        await service.AssignToRoleAsync("admin", ["1", "2", "3"]);

        {
            await using var db = new TestDbContext(options);
            var assigned = await db.RoleMenus
                .Where(rm => rm.RoleCode == "admin")
                .Select(rm => rm.MenuId)
                .ToListAsync();
            assigned.Should().BeEquivalentTo(["1", "2", "3"]);
        }
    }

    [Fact]
    public async Task AssignToRoleAsync_RemoveOldAssignments()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.RoleMenus.Add(new TenE0RoleMenu { RoleCode = "admin", MenuId = "1" });
            db.RoleMenus.Add(new TenE0RoleMenu { RoleCode = "admin", MenuId = "2" });
            db.RoleMenus.Add(new TenE0RoleMenu { RoleCode = "admin", MenuId = "3" });
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        // Keep only "1", remove "2" and "3"
        await service.AssignToRoleAsync("admin", ["1"]);

        {
            await using var db = new TestDbContext(options);
            var assigned = await db.RoleMenus
                .Where(rm => rm.RoleCode == "admin")
                .Select(rm => rm.MenuId)
                .ToListAsync();
            assigned.Should().BeEquivalentTo(["1"]);
        }
    }

    [Fact]
    public async Task AssignToRoleAsync_DiffUnchanged_NoAddRemove()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.RoleMenus.Add(new TenE0RoleMenu { RoleCode = "admin", MenuId = "1" });
            db.RoleMenus.Add(new TenE0RoleMenu { RoleCode = "admin", MenuId = "2" });
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        // Same set — no change
        await service.AssignToRoleAsync("admin", ["1", "2"]);

        {
            await using var db = new TestDbContext(options);
            var count = await db.RoleMenus
                .Where(rm => rm.RoleCode == "admin")
                .CountAsync();
            count.Should().Be(2);
        }
    }

    [Fact]
    public async Task AssignToRoleAsync_EmptyMenuIds_RemovesAll()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.RoleMenus.Add(new TenE0RoleMenu { RoleCode = "admin", MenuId = "1" });
            db.RoleMenus.Add(new TenE0RoleMenu { RoleCode = "admin", MenuId = "2" });
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        await service.AssignToRoleAsync("admin", []);

        {
            await using var db = new TestDbContext(options);
            var count = await db.RoleMenus
                .Where(rm => rm.RoleCode == "admin")
                .CountAsync();
            count.Should().Be(0);
        }
    }

    // ──────────────────────────────────────────────
    // GetRoleMenuIdsAsync Tests
    // ──────────────────────────────────────────────

    [Fact]
    public async Task GetRoleMenuIdsAsync_ReturnsAssignedIds()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.RoleMenus.Add(new TenE0RoleMenu { RoleCode = "admin", MenuId = "1" });
            db.RoleMenus.Add(new TenE0RoleMenu { RoleCode = "admin", MenuId = "2" });
            db.RoleMenus.Add(new TenE0RoleMenu { RoleCode = "editor", MenuId = "3" });
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        var result = await service.GetRoleMenuIdsAsync("admin");

        result.Should().BeEquivalentTo(new HashSet<string> { "1", "2" });
    }

    [Fact]
    public async Task GetRoleMenuIdsAsync_EmptyRole_ReturnsEmptySet()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;

        {
            await using var db = new TestDbContext(options);
            db.RoleMenus.Add(new TenE0RoleMenu { RoleCode = "admin", MenuId = "1" });
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        var result = await service.GetRoleMenuIdsAsync("nonexistent");

        result.Should().BeEmpty();
    }
}
