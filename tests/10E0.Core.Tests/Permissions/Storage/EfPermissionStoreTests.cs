using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TenE0.Core.Permissions;
using TenE0.Core.Permissions.Storage;

namespace TenE0.Core.Tests.Permissions.Storage;

[Trait("Category", "Unit")]
public sealed class EfPermissionStoreTests
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

    [Fact]
    public async Task GetGrantedPermissionsAsync_ReturnsPermissionKeys()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.RolePermissions.Add(new TenE0RolePermission { RoleCode = "admin", PermissionKey = "user.read" });
            ctx.RolePermissions.Add(new TenE0RolePermission { RoleCode = "admin", PermissionKey = "user.write" });
            ctx.RolePermissions.Add(new TenE0RolePermission { RoleCode = "viewer", PermissionKey = "user.read" });
            await ctx.SaveChangesAsync();
        }

        var store = new EfPermissionStore<TestDbContext>(factory);

        var result = await store.GetGrantedPermissionsAsync(["admin", "viewer"]);

        result.Should().BeEquivalentTo("user.read", "user.write");
    }

    [Fact]
    public async Task GetGrantedPermissionsAsync_EmptyRoles_ReturnsEmpty()
    {
        var store = new EfPermissionStore<TestDbContext>(CreateFactory(Guid.NewGuid().ToString("N")));

        var result = await store.GetGrantedPermissionsAsync([]);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetGrantedPermissionsAsync_NoMatch_ReturnsEmpty()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.RolePermissions.Add(new TenE0RolePermission { RoleCode = "admin", PermissionKey = "p1" });
            await ctx.SaveChangesAsync();
        }

        var store = new EfPermissionStore<TestDbContext>(factory);

        var result = await store.GetGrantedPermissionsAsync(["nonexistent"]);

        result.Should().BeEmpty();
    }
}
