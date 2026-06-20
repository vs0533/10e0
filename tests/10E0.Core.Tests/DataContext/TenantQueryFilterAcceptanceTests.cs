using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;
using TenE0.Core.Abstractions;
using TenE0.Core.DataContext;
using TenE0.Core.DynamicFilters;
using TenE0.Core.Permissions.DataFilter;

namespace TenE0.Core.Tests.DataContext;

/// <summary>
/// BDD acceptance tests for #11 — Part 3 (Query Filter).
///
/// 验证 BaseDataContext 在 OnModelCreating 时为所有实现
/// <see cref="IMultiTenantEntity"/> 的实体自动注册名为 "Tenant" 的
/// Named Query Filter，跨租户查询自动隔离。
///
/// 验收点：
/// 1. 自动注册（不需业务侧手动写 modelBuilder.Entity&lt;X&gt;().HasQueryFilter(...)）
/// 2. 同租户：看得见
/// 3. 跨租户：看不见
/// 4. 超管（BypassFilters=true）：看得见全部
/// 5. .IgnoreQueryFilters("Tenant")：旁路租户过滤
/// 6. 非多租户实体：不注册 Tenant 过滤器
/// </summary>
[Trait("Category", "BDD")]
public sealed class TenantQueryFilterAcceptanceTests
{
    // ── Test entities ─────────────────────────────────────────────

    private sealed class TenantDocument : IMultiTenantEntity
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string TenantId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
    }

    private sealed class NonTenantNote : IBaseEntity
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Body { get; set; } = string.Empty;
    }

    private sealed class SoftDeleteTenantDoc : IMultiTenantEntity, ISoftDeleteEntity
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string TenantId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public bool IsSoftDelete { get; set; }
        public DateTimeOffset? DeleteTime { get; set; }
        public string? DeleteBy { get; set; }
    }

    // ── DbContext subclass with a tenant context binding ──────────

    private sealed class TestTenantContext(
        DbContextOptions<TestTenantContext> options,
        ICurrentUserContext user,
        IDataAccessPolicy policy,
        IEnumerable<IEntityFilterContributor> contributors,
        IDynamicFilterProvider provider,
        ITenantContext tenantContext) : BaseDataContext(options, user, policy, contributors, provider, tenantContext)
    {
        public ITenantContext TenantContext { get; } = tenantContext;
        public DbSet<TenantDocument> Documents => Set<TenantDocument>();
        public DbSet<NonTenantNote> Notes => Set<NonTenantNote>();
        public DbSet<SoftDeleteTenantDoc> SoftDocs => Set<SoftDeleteTenantDoc>();
    }

    /// <summary>
    /// Defeats EF Core's in-memory model cache so each test gets a fresh
    /// OnModelCreating execution (mirrors BaseDataContextTests pattern).
    /// </summary>
    private sealed class InstanceModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => context;
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static Mock<ICurrentUserContext> CreateUser()
    {
        var mock = new Mock<ICurrentUserContext>();
        mock.SetupGet(c => c.IsAuthenticated).Returns(true);
        mock.SetupGet(c => c.UserCode).Returns("u1");
        mock.SetupGet(c => c.RoleIds).Returns(Array.Empty<string>());
        return mock;
    }

    private static Mock<IDataAccessPolicy> CreatePolicy(bool bypass = false)
    {
        var mock = new Mock<IDataAccessPolicy>();
        mock.SetupGet(p => p.BypassFilters).Returns(bypass);
        return mock;
    }

    private static Mock<IDynamicFilterProvider> CreateDynamicProvider() => new();

    private static Mock<ITenantContext> CreateTenantContext(string? tenantId)
    {
        var mock = new Mock<ITenantContext>();
        mock.SetupGet(t => t.TenantId).Returns(tenantId);
        return mock;
    }

    private static DbContextOptions<TestTenantContext> NewInMemoryOptions() =>
        new DbContextOptionsBuilder<TestTenantContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ReplaceService<IModelCacheKeyFactory, InstanceModelCacheKeyFactory>()
            .Options;

    /// <summary>
    /// EF InMemory provider does not enforce query filters — runtime cross-tenant
    /// isolation tests need a real relational provider. SQLite in-memory is the
    /// lightest option that honors <c>HasQueryFilter</c>.
    ///
    /// The returned helper owns a shared SqliteConnection; passing the same
    /// connection into <see cref="SqliteDbContextOptionsExtensions.UseSqlite"/>
    /// makes seed + query contexts see the same data. The connection is held
    /// open until <see cref="SqliteRuntimeHandle.Dispose"/> is called (test end).
    /// </summary>
    private static SqliteRuntimeHandle NewSqlite()
    {
        // Keep the in-memory database alive for the duration of the test by
        // holding an open SqliteConnection. The EF Core docs cover this pattern:
        // https://learn.microsoft.com/ef/core/testing/sqlite#in-memory-sqlite
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<TestTenantContext>()
            .UseSqlite(connection)
            .Options;

        return new SqliteRuntimeHandle(connection, options);
    }

    /// <summary>
    /// Tuple-like holder: the connection is held open to keep the in-memory DB alive,
    /// options are reused across seed + query contexts.
    /// </summary>
    private sealed class SqliteRuntimeHandle(
        SqliteConnection connection,
        DbContextOptions<TestTenantContext> options) : IDisposable
    {
        public DbContextOptions<TestTenantContext> Options => options;
        public void Dispose()
        {
            try { connection.Close(); } catch { /* noop */ }
            connection.Dispose();
        }
    }

    // ── OnModelCreating: Tenant filter registration ───────────────

    [Fact]
    public void GivenEntityImplementsIMultiTenantEntity_WhenModelIsBuilt_ThenNamedTenantFilterIsRegistered()
    {
        // Arrange
        using var ctx = new TestTenantContext(
            NewInMemoryOptions(),
            CreateUser().Object,
            CreatePolicy().Object,
            [],
            CreateDynamicProvider().Object,
            CreateTenantContext("t-a").Object);

        // Act
        var entityType = ctx.Model.FindEntityType(typeof(TenantDocument));

        // Assert
        entityType.Should().NotBeNull();
        entityType!.FindDeclaredQueryFilter("Tenant").Should().NotBeNull(
            "the framework must auto-register a 'Tenant' named query filter for IMultiTenantEntity implementers");
    }

    [Fact]
    public void GivenEntityNotImplementingIMultiTenantEntity_WhenModelIsBuilt_ThenNoTenantFilterIsRegistered()
    {
        // Arrange
        using var ctx = new TestTenantContext(
            NewInMemoryOptions(),
            CreateUser().Object,
            CreatePolicy().Object,
            [],
            CreateDynamicProvider().Object,
            CreateTenantContext("t-a").Object);

        // Act
        var entityType = ctx.Model.FindEntityType(typeof(NonTenantNote));

        // Assert
        entityType.Should().NotBeNull();
        entityType!.FindDeclaredQueryFilter("Tenant").Should().BeNull(
            "non-tenant entities must not have a Tenant filter — it would have nothing to compare against");
    }

    [Fact]
    public void GivenTenantEntityAlsoImplementsISoftDelete_WhenModelIsBuilt_ThenBothSoftDeleteAndTenantFiltersAreRegistered()
    {
        // Arrange
        using var ctx = new TestTenantContext(
            NewInMemoryOptions(),
            CreateUser().Object,
            CreatePolicy().Object,
            [],
            CreateDynamicProvider().Object,
            CreateTenantContext("t-a").Object);

        // Act
        var entityType = ctx.Model.FindEntityType(typeof(SoftDeleteTenantDoc));

        // Assert — both filters coexist (AND-composed at query time)
        entityType.Should().NotBeNull();
        entityType!.FindDeclaredQueryFilter("SoftDelete").Should().NotBeNull();
        entityType.FindDeclaredQueryFilter("Tenant").Should().NotBeNull();
    }

    // ── Runtime: cross-tenant isolation ───────────────────────────

    [Fact]
    public async Task GivenTenantContextSet_WhenQueryingTenantEntity_ThenOnlyCurrentTenantRowsAreVisible()
    {
        // Arrange
        using var sqlite = NewSqlite();
        var tenantA = CreateTenantContext("t-a").Object;
        using (var seed = new TestTenantContext(
            sqlite.Options, CreateUser().Object, CreatePolicy().Object, [],
            CreateDynamicProvider().Object, tenantA))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Documents.Add(new TenantDocument { Id = "1", TenantId = "t-a", Title = "A1" });
            seed.Documents.Add(new TenantDocument { Id = "2", TenantId = "t-a", Title = "A2" });
            seed.Documents.Add(new TenantDocument { Id = "3", TenantId = "t-b", Title = "B1" });
            await seed.SaveChangesAsync();
        }

        // Act — query from tenant A's context
        using var ctxA = new TestTenantContext(
            sqlite.Options, CreateUser().Object, CreatePolicy().Object, [],
            CreateDynamicProvider().Object, tenantA);

        var visibleTitles = await ctxA.Documents.Select(d => d.Title).ToListAsync();

        // Assert — only A's rows
        visibleTitles.Should().BeEquivalentTo(new[] { "A1", "A2" },
            "the Tenant query filter must restrict reads to the current tenant");
    }

    [Fact]
    public async Task GivenTenantContextSetToB_WhenQueryingTenantEntity_ThenTenantARowsAreHidden()
    {
        // Arrange — seed under tenant A's context
        using var sqlite = NewSqlite();
        var tenantA = CreateTenantContext("t-a").Object;
        var tenantB = CreateTenantContext("t-b").Object;
        using (var seed = new TestTenantContext(
            sqlite.Options, CreateUser().Object, CreatePolicy().Object, [],
            CreateDynamicProvider().Object, tenantA))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Documents.Add(new TenantDocument { Id = "1", TenantId = "t-a", Title = "A1" });
            seed.Documents.Add(new TenantDocument { Id = "2", TenantId = "t-b", Title = "B1" });
            await seed.SaveChangesAsync();
        }

        // Act — switch to tenant B and read
        using var ctxB = new TestTenantContext(
            sqlite.Options, CreateUser().Object, CreatePolicy().Object, [],
            CreateDynamicProvider().Object, tenantB);

        var visible = await ctxB.Documents.Select(d => d.Title).ToListAsync();

        // Assert
        visible.Should().ContainSingle().Which.Should().Be("B1",
            "tenant B must not see tenant A's documents — this is the core security guarantee");
    }

    [Fact]
    public async Task GivenTenantContextIsNull_WhenQueryingTenantEntity_ThenNoRowsAreVisible()
    {
        // Arrange — seed with tenant A
        using var sqlite = NewSqlite();
        var tenantA = CreateTenantContext("t-a").Object;
        var tenantNone = CreateTenantContext(null).Object;
        using (var seed = new TestTenantContext(
            sqlite.Options, CreateUser().Object, CreatePolicy().Object, [],
            CreateDynamicProvider().Object, tenantA))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Documents.Add(new TenantDocument { Id = "1", TenantId = "t-a", Title = "A1" });
            await seed.SaveChangesAsync();
        }

        // Act — read from a context that has no tenant (unauthenticated background job)
        using var ctx = new TestTenantContext(
            sqlite.Options, CreateUser().Object, CreatePolicy().Object, [],
            CreateDynamicProvider().Object, tenantNone);

        var visible = await ctx.Documents.ToListAsync();

        // Assert
        visible.Should().BeEmpty(
            "with TenantId == null the filter must hide everything (safe-by-default for background jobs)");
    }

    // ── Runtime: super-admin bypass ───────────────────────────────

    [Fact]
    public async Task GivenBypassFiltersTrue_WhenQueryingTenantEntity_ThenAllTenantsRowsAreVisible()
    {
        // Arrange — seed under any tenant (filters don't apply on seed since policy controls query)
        using var sqlite = NewSqlite();
        using (var seed = new TestTenantContext(
            sqlite.Options, CreateUser().Object, CreatePolicy().Object, [],
            CreateDynamicProvider().Object, CreateTenantContext("t-a").Object))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Documents.Add(new TenantDocument { Id = "1", TenantId = "t-a", Title = "A1" });
            seed.Documents.Add(new TenantDocument { Id = "2", TenantId = "t-b", Title = "B1" });
            await seed.SaveChangesAsync();
        }

        // Act — admin (BypassFilters=true) reads; even if their tenant is "t-a", they see B1 too
        using var adminCtx = new TestTenantContext(
            sqlite.Options, CreateUser().Object, CreatePolicy(bypass: true).Object, [],
            CreateDynamicProvider().Object, CreateTenantContext("t-a").Object);

        var visible = await adminCtx.Documents.Select(d => d.Title).ToListAsync();

        // Assert
        visible.Should().BeEquivalentTo(new[] { "A1", "B1" },
            "super-admin with BypassFilters=true must see all tenants (cross-tenant audit / support)");
    }

    [Fact]
    public async Task GivenBypassFiltersFalse_WhenQueryingTenantEntity_ThenOnlyCurrentTenantRowsAreVisible()
    {
        // Arrange — same as bypass test, but policy says "do not bypass"
        using var sqlite = NewSqlite();
        using (var seed = new TestTenantContext(
            sqlite.Options, CreateUser().Object, CreatePolicy().Object, [],
            CreateDynamicProvider().Object, CreateTenantContext("t-a").Object))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Documents.Add(new TenantDocument { Id = "1", TenantId = "t-a", Title = "A1" });
            seed.Documents.Add(new TenantDocument { Id = "2", TenantId = "t-b", Title = "B1" });
            await seed.SaveChangesAsync();
        }

        // Act
        using var ctx = new TestTenantContext(
            sqlite.Options, CreateUser().Object, CreatePolicy(bypass: false).Object, [],
            CreateDynamicProvider().Object, CreateTenantContext("t-a").Object);

        var visible = await ctx.Documents.Select(d => d.Title).ToListAsync();

        // Assert
        visible.Should().BeEquivalentTo(new[] { "A1" },
            "non-admin must remain tenant-scoped even when the data contains other tenants");
    }
}
