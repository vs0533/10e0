using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TenE0.Core.Permissions;
using TenE0.Core.Permissions.Management;
using TenE0.Core.Permissions.Storage;
using Moq;

namespace TenE0.Core.Tests.Permissions.Management;

[Trait("Category", "Unit")]
public sealed class PermissionGrantServiceTests
{
    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TenE0RolePermission> RolePermissions => Set<TenE0RolePermission>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TenE0RolePermission>(b =>
            {
                b.HasKey(nameof(TenE0RolePermission.RoleCode), nameof(TenE0RolePermission.PermissionKey));
            });
        }
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            optionsBuilder.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<TestDbContext> options) : IDbContextFactory<TestDbContext>
    {
        public TestDbContext CreateDbContext() => new(options);
    }

    private static IDbContextFactory<TestDbContext> CreateFactory(string dbName)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TestDbContextFactory(options);
    }

    private static PermissionCatalog CreateCatalog(params string[] keys)
    {
        var provider = new TestPermissionProvider(keys.Select(k => new PermissionDefinition(k, k)));
        return new PermissionCatalog([provider]);
    }

    private sealed class TestPermissionProvider(IEnumerable<PermissionDefinition> defs) : IPermissionProvider
    {
        public IEnumerable<PermissionDefinition> Define() => defs;
    }

    [Fact]
    public async Task GrantAsync_NewPermission_CreatesRecordAndInvalidatesCache()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var catalog = CreateCatalog("user.read", "user.write");
        var cacheMock = new Mock<IPermissionCache>();
        var svc = new PermissionGrantService<TestDbContext>(factory, catalog, cacheMock.Object);

        await svc.GrantAsync("admin", "user.read");

        await using var ctx = factory.CreateDbContext();
        var rows = await ctx.RolePermissions.ToListAsync();
        rows.Should().ContainSingle();
        rows[0].RoleCode.Should().Be("admin");
        rows[0].PermissionKey.Should().Be("user.read");
        cacheMock.Verify(c => c.InvalidateRoleAsync("admin", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GrantAsync_ExistingPermission_Noop()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        await using (var seedCtx = factory.CreateDbContext())
        {
            seedCtx.RolePermissions.Add(new TenE0RolePermission { RoleCode = "admin", PermissionKey = "user.read" });
            await seedCtx.SaveChangesAsync();
        }

        var catalog = CreateCatalog("user.read");
        var cacheMock = new Mock<IPermissionCache>();
        var svc = new PermissionGrantService<TestDbContext>(factory, catalog, cacheMock.Object);

        await svc.GrantAsync("admin", "user.read");

        await using var verifyCtx = factory.CreateDbContext();
        var rows = await verifyCtx.RolePermissions.ToListAsync();
        rows.Should().ContainSingle();
        cacheMock.Verify(c => c.InvalidateRoleAsync("admin", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GrantAsync_UndefinedKey_ThrowsInvalidOperation()
    {
        var factory = CreateFactory(Guid.NewGuid().ToString("N"));
        var catalog = CreateCatalog("user.read");
        var svc = new PermissionGrantService<TestDbContext>(factory, catalog, Mock.Of<IPermissionCache>());

        var act = () => svc.GrantAsync("admin", "undefined.key");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*未在 PermissionCatalog 中定义*");
    }

    [Fact]
    public async Task RevokeAsync_ExistingPermission_RemovesRecordAndInvalidatesCache()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        await using (var seedCtx = factory.CreateDbContext())
        {
            seedCtx.RolePermissions.Add(new TenE0RolePermission { RoleCode = "admin", PermissionKey = "user.read" });
            await seedCtx.SaveChangesAsync();
        }

        var catalog = CreateCatalog("user.read");
        var cacheMock = new Mock<IPermissionCache>();
        var svc = new PermissionGrantService<TestDbContext>(factory, catalog, cacheMock.Object);

        await svc.RevokeAsync("admin", "user.read");

        await using var verifyCtx = factory.CreateDbContext();
        var rows = await verifyCtx.RolePermissions.ToListAsync();
        rows.Should().BeEmpty();
        cacheMock.Verify(c => c.InvalidateRoleAsync("admin", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RevokeAsync_NonExistent_Noop()
    {
        var factory = CreateFactory(Guid.NewGuid().ToString("N"));
        var catalog = CreateCatalog("user.read");
        var cacheMock = new Mock<IPermissionCache>();
        var svc = new PermissionGrantService<TestDbContext>(factory, catalog, cacheMock.Object);

        await svc.RevokeAsync("admin", "user.read");

        cacheMock.Verify(c => c.InvalidateRoleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetGrantsAsync_ReplacesPermissions()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        await using (var seedCtx = factory.CreateDbContext())
        {
            seedCtx.RolePermissions.Add(new TenE0RolePermission { RoleCode = "admin", PermissionKey = "old.perm" });
            await seedCtx.SaveChangesAsync();
        }

        var catalog = CreateCatalog("new.perm", "old.perm");
        var cacheMock = new Mock<IPermissionCache>();
        var svc = new PermissionGrantService<TestDbContext>(factory, catalog, cacheMock.Object);

        await svc.SetGrantsAsync("admin", ["new.perm"]);

        await using var verifyCtx = factory.CreateDbContext();
        var rows = await verifyCtx.RolePermissions.ToListAsync();
        rows.Should().ContainSingle();
        rows[0].PermissionKey.Should().Be("new.perm");
        cacheMock.Verify(c => c.InvalidateRoleAsync("admin", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListGrantedAsync_ReturnsSortedKeys()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.RolePermissions.Add(new TenE0RolePermission { RoleCode = "admin", PermissionKey = "z.last" });
            ctx.RolePermissions.Add(new TenE0RolePermission { RoleCode = "admin", PermissionKey = "a.first" });
            await ctx.SaveChangesAsync();
        }

        var catalog = CreateCatalog("a.first", "z.last");
        var svc = new PermissionGrantService<TestDbContext>(factory, catalog, Mock.Of<IPermissionCache>());

        var result = await svc.ListGrantedAsync("admin");

        result.Should().Equal("a.first", "z.last");
    }

    [Fact]
    public async Task ListGrantedAsync_NoPermissions_ReturnsEmpty()
    {
        var factory = CreateFactory(Guid.NewGuid().ToString("N"));
        var svc = new PermissionGrantService<TestDbContext>(factory, CreateCatalog(), Mock.Of<IPermissionCache>());

        var result = await svc.ListGrantedAsync("role1");

        result.Should().BeEmpty();
    }
}
