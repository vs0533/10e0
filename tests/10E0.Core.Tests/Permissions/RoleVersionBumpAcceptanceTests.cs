using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TenE0.Core.Permissions;
using TenE0.Core.Permissions.Management;
using TenE0.Core.Permissions.Storage;

namespace TenE0.Core.Tests.Permissions;

/// <summary>
/// BDD-style acceptance tests for #7 — verifies that
/// <see cref="IPermissionGrantService"/> write paths bump the
/// <c>TenE0Role.Version</c> counter, so the version-check on
/// <see cref="IPermissionEvaluator.HasAsync"/> can detect the change.
///
/// Tests use the same EF Core InMemory pattern as
/// <c>PermissionGrantServiceTests</c>. They expect a new
/// <c>TenE0Role.Version</c> property to exist on the role entity.
/// </summary>
[Trait("Category", "BDD")]
public sealed class RoleVersionBumpAcceptanceTests
{
    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TenE0RolePermission> RolePermissions => Set<TenE0RolePermission>();
        public DbSet<TenE0Role> Roles => Set<TenE0Role>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TenE0RolePermission>(b =>
            {
                b.HasKey(nameof(TenE0RolePermission.RoleCode),
                         nameof(TenE0RolePermission.PermissionKey));
            });
            modelBuilder.Entity<TenE0Role>(b =>
            {
                b.HasKey(r => r.Code);
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

    private static async Task<long> ReadRoleVersionAsync(IDbContextFactory<TestDbContext> f, string roleCode)
    {
        await using var ctx = f.CreateDbContext();
        var role = await ctx.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Code == roleCode);
        return role?.Version ?? 0L;
    }

    private static async Task SeedRoleAsync(IDbContextFactory<TestDbContext> f, TenE0Role role)
    {
        await using var ctx = f.CreateDbContext();
        ctx.Roles.Add(role);
        await ctx.SaveChangesAsync();
    }

    // ── Acceptance: Grant bumps version ────────────────────────

    [Fact]
    public async Task GivenRoleWithVersionOne_WhenGrantingNewPermission_ThenVersionIncrementsToTwo()
    {
        // Arrange
        var factory = CreateFactory(Guid.NewGuid().ToString("N"));
        await SeedRoleAsync(factory, new TenE0Role { Code = "editor", Name = "Editor", Version = 1L });
        var catalog = CreateCatalog("demo.update");
        var svc = new PermissionGrantService<TestDbContext>(
            factory, catalog, Mock.Of<IPermissionCache>());

        // Act
        await svc.GrantAsync("editor", "demo.update");

        // Assert
        var version = await ReadRoleVersionAsync(factory, "editor");
        version.Should().Be(2L, "every successful grant must bump the role version");
    }

    [Fact]
    public async Task GivenRoleWithVersionOne_WhenGrantingDuplicatePermission_ThenVersionStaysUnchanged()
    {
        // Arrange — first grant is the bootstrap
        var factory = CreateFactory(Guid.NewGuid().ToString("N"));
        await SeedRoleAsync(factory, new TenE0Role { Code = "editor", Name = "Editor", Version = 1L });
        var catalog = CreateCatalog("demo.update");
        var svc = new PermissionGrantService<TestDbContext>(
            factory, catalog, Mock.Of<IPermissionCache>());

        await svc.GrantAsync("editor", "demo.update");
        var versionAfterFirst = await ReadRoleVersionAsync(factory, "editor");

        // Act — re-grant the same permission (idempotent no-op)
        await svc.GrantAsync("editor", "demo.update");

        // Assert
        var versionAfterDuplicate = await ReadRoleVersionAsync(factory, "editor");
        versionAfterDuplicate.Should().Be(
            versionAfterFirst,
            "idempotent re-grant is a no-op and must not bump the version (avoids unnecessary cache invalidation)");
    }

    // ── Acceptance: Revoke bumps version ──────────────────────

    [Fact]
    public async Task GivenRoleWithExistingGrant_WhenRevokingPermission_ThenVersionIncrements()
    {
        // Arrange
        var factory = CreateFactory(Guid.NewGuid().ToString("N"));
        await SeedRoleAsync(factory, new TenE0Role { Code = "editor", Name = "Editor", Version = 5L });
        await using (var seed = factory.CreateDbContext())
        {
            seed.RolePermissions.Add(new TenE0RolePermission
            {
                RoleCode = "editor",
                PermissionKey = "demo.update",
            });
            await seed.SaveChangesAsync();
        }
        var catalog = CreateCatalog("demo.update");
        var svc = new PermissionGrantService<TestDbContext>(
            factory, catalog, Mock.Of<IPermissionCache>());

        // Act
        await svc.RevokeAsync("editor", "demo.update");

        // Assert
        var version = await ReadRoleVersionAsync(factory, "editor");
        version.Should().Be(6L, "revoke must bump version so any in-flight token is invalidated");
    }

    [Fact]
    public async Task GivenRoleWithoutThatGrant_WhenRevokingNonexistentPermission_ThenVersionStaysUnchanged()
    {
        // Arrange
        var factory = CreateFactory(Guid.NewGuid().ToString("N"));
        await SeedRoleAsync(factory, new TenE0Role { Code = "editor", Name = "Editor", Version = 3L });
        var catalog = CreateCatalog("demo.update");
        var svc = new PermissionGrantService<TestDbContext>(
            factory, catalog, Mock.Of<IPermissionCache>());

        // Act
        await svc.RevokeAsync("editor", "demo.update");

        // Assert
        var version = await ReadRoleVersionAsync(factory, "editor");
        version.Should().Be(3L,
            "revoke on a missing grant is a no-op and must not bump the version");
    }

    // ── Acceptance: SetGrants bumps version (bulk replace) ────

    [Fact]
    public async Task GivenRoleWithExistingGrants_WhenSetGrantsReplacesList_ThenVersionIncrements()
    {
        // Arrange
        var factory = CreateFactory(Guid.NewGuid().ToString("N"));
        await SeedRoleAsync(factory, new TenE0Role { Code = "editor", Name = "Editor", Version = 2L });
        await using (var seed = factory.CreateDbContext())
        {
            seed.RolePermissions.Add(new TenE0RolePermission
            {
                RoleCode = "editor",
                PermissionKey = "old.perm",
            });
            await seed.SaveChangesAsync();
        }
        var catalog = CreateCatalog("new.perm", "old.perm");
        var svc = new PermissionGrantService<TestDbContext>(
            factory, catalog, Mock.Of<IPermissionCache>());

        // Act
        await svc.SetGrantsAsync("editor", new[] { "new.perm" });

        // Assert
        var version = await ReadRoleVersionAsync(factory, "editor");
        version.Should().Be(3L, "bulk replace is a meaningful change and must bump version");
    }
}
