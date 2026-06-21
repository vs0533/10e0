using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using TenE0.Core.Abstractions;
using TenE0.Core.DataContext;
using TenE0.Core.DynamicFilters;
using TenE0.Core.Permissions.DataFilter;

namespace TenE0.Core.Tests.DataContext;

[Trait("Category", "Unit")]
public sealed class BaseDataContextTests
{
    // ── Test entities ──────────────────────────────────────────────────

    private sealed class SoftDeletableEntity : IBaseEntity, ISoftDeleteEntity
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string? Name { get; set; }
        public bool IsSoftDelete { get; set; }
        public DateTimeOffset? DeleteTime { get; set; }
        public string? DeleteBy { get; set; }
    }

    private sealed class PlainEntity : IBaseEntity
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string? Name { get; set; }
    }

    private sealed class SoftDeletableWithFilter : IBaseEntity, ISoftDeleteEntity
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string? Name { get; set; }
        public bool IsSoftDelete { get; set; }
        public DateTimeOffset? DeleteTime { get; set; }
        public string? DeleteBy { get; set; }
        public string? OrgId { get; set; }
    }

    // ── Entity filter contributor types ────────────────────────────────

    private sealed class TestEntityFilterContributor : IEntityFilterContributor
    {
        public Type EntityType => typeof(SoftDeletableWithFilter);
        public LambdaExpression? BuildFilter(BaseDataContext context) =>
            (Expression<Func<SoftDeletableWithFilter, bool>>)(e => e.OrgId == "x");
    }

    private sealed class NoOpFilterContributor : IEntityFilterContributor
    {
        public Type EntityType => typeof(PlainEntity);
        public LambdaExpression? BuildFilter(BaseDataContext context) => null;
    }

    private sealed class AnotherContributor : IEntityFilterContributor
    {
        public Type EntityType => typeof(SoftDeletableWithFilter);
        public LambdaExpression? BuildFilter(BaseDataContext context) =>
            (Expression<Func<SoftDeletableWithFilter, bool>>)(e => e.OrgId == "y");
    }

    // ── Per-test DbContext subclasses (defeat EF model cache) ─────────

    private sealed class EmptyContext(
        DbContextOptions<EmptyContext> options,
        ICurrentUserContext user,
        IDataAccessPolicy policy,
        IEnumerable<IEntityFilterContributor> contributors,
        IDynamicFilterProvider provider,
        ITenantContext tenantContext) : BaseDataContext(options, user, policy, contributors, provider, tenantContext)
    {
        public DbSet<SoftDeletableEntity> SoftEntities => Set<SoftDeletableEntity>();
        public DbSet<PlainEntity> PlainEntities => Set<PlainEntity>();
    }

    private sealed class FilteredContext(
        DbContextOptions<FilteredContext> options,
        ICurrentUserContext user,
        IDataAccessPolicy policy,
        IEnumerable<IEntityFilterContributor> contributors,
        IDynamicFilterProvider provider,
        ITenantContext tenantContext) : BaseDataContext(options, user, policy, contributors, provider, tenantContext)
    {
        public DbSet<SoftDeletableWithFilter> FilteredEntities => Set<SoftDeletableWithFilter>();
    }

    /// <summary>
    /// Returns a unique model cache key per DbContext instance, defeating the
    /// EF Core in-memory model cache so each test gets a fresh OnModelCreating.
    /// </summary>
    private sealed class InstanceModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => context;
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static Mock<ICurrentUserContext> CreateUser(
        string? userCode = "u1",
        IReadOnlyList<string>? roleIds = null,
        bool authenticated = true)
    {
        var mock = new Mock<ICurrentUserContext>();
        mock.SetupGet(c => c.IsAuthenticated).Returns(authenticated);
        mock.SetupGet(c => c.UserCode).Returns(userCode);
        mock.SetupGet(c => c.RoleIds).Returns(roleIds ?? Array.Empty<string>());
        return mock;
    }

    private static Mock<IDataAccessPolicy> CreatePolicy(bool bypass = false)
    {
        var mock = new Mock<IDataAccessPolicy>();
        mock.SetupGet(p => p.BypassFilters).Returns(bypass);
        return mock;
    }

    private static DbContextOptions<TContext> NewInMemoryOptions<TContext>() where TContext : DbContext
        => new DbContextOptionsBuilder<TContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ReplaceService<IModelCacheKeyFactory, InstanceModelCacheKeyFactory>()
            .Options;

    // ── Runtime property pass-through ─────────────────────────────────

    [Fact]
    public void CurrentUserCode_Delegates_ToICurrentUserContext()
    {
        using var ctx = new EmptyContext(
            NewInMemoryOptions<EmptyContext>(),
            CreateUser().Object,
            CreatePolicy().Object,
            [],
            new Mock<IDynamicFilterProvider>().Object,
            new Mock<ITenantContext>().Object);

        ctx.CurrentUserCode.Should().Be("u1");
    }

    [Fact]
    public void CurrentUserCode_ReturnsNull_WhenUserNotAuthenticated()
    {
        using var ctx = new EmptyContext(
            NewInMemoryOptions<EmptyContext>(),
            CreateUser(userCode: null, authenticated: false).Object,
            CreatePolicy().Object,
            [],
            new Mock<IDynamicFilterProvider>().Object,
            new Mock<ITenantContext>().Object);

        ctx.CurrentUserCode.Should().BeNull();
    }

    [Fact]
    public void CurrentRoleIds_Cached_OnConstruction()
    {
        var roleIds = new[] { "r1", "r2" };
        using var ctx = new EmptyContext(
            NewInMemoryOptions<EmptyContext>(),
            CreateUser(roleIds: roleIds).Object,
            CreatePolicy().Object,
            [],
            new Mock<IDynamicFilterProvider>().Object,
            new Mock<ITenantContext>().Object);

        ctx.CurrentRoleIds.Should().BeEquivalentTo(roleIds);
    }

    [Fact]
    public void CurrentOrgIds_DefaultsToEmpty_AndIsMutable()
    {
        using var ctx = new EmptyContext(
            NewInMemoryOptions<EmptyContext>(),
            CreateUser().Object,
            CreatePolicy().Object,
            [],
            new Mock<IDynamicFilterProvider>().Object,
            new Mock<ITenantContext>().Object);

        ctx.CurrentOrgIds.Should().BeEmpty();
        ctx.CurrentOrgIds = ["o1"];
        ctx.CurrentOrgIds.Should().BeEquivalentTo(["o1"]);
    }

    [Fact]
    public void IsAuthenticated_Delegates_ToICurrentUserContext()
    {
        using var ctx = new EmptyContext(
            NewInMemoryOptions<EmptyContext>(),
            CreateUser(authenticated: false).Object,
            CreatePolicy().Object,
            [],
            new Mock<IDynamicFilterProvider>().Object,
            new Mock<ITenantContext>().Object);

        ctx.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public void BypassFilters_Delegates_ToAccessPolicy()
    {
        using var ctx = new EmptyContext(
            NewInMemoryOptions<EmptyContext>(),
            CreateUser().Object,
            CreatePolicy(bypass: true).Object,
            [],
            new Mock<IDynamicFilterProvider>().Object,
            new Mock<ITenantContext>().Object);

        ctx.BypassFilters.Should().BeTrue();
    }

    // ── OnModelCreating: SoftDelete ───────────────────────────────────

    [Fact]
    public void OnModelCreating_SoftDeleteEntity_RegistersSoftDeleteFilter()
    {
        using var ctx = new EmptyContext(
            NewInMemoryOptions<EmptyContext>(),
            CreateUser().Object,
            CreatePolicy().Object,
            [],
            new Mock<IDynamicFilterProvider>().Object,
            new Mock<ITenantContext>().Object);

        var entityType = ctx.Model.FindEntityType(typeof(SoftDeletableEntity));
        entityType.Should().NotBeNull();
        entityType!.FindDeclaredQueryFilter("SoftDelete").Should().NotBeNull();
    }

    [Fact]
    public void OnModelCreating_NonSoftDeleteEntity_DoesNotRegisterSoftDeleteFilter()
    {
        using var ctx = new EmptyContext(
            NewInMemoryOptions<EmptyContext>(),
            CreateUser().Object,
            CreatePolicy().Object,
            [],
            new Mock<IDynamicFilterProvider>().Object,
            new Mock<ITenantContext>().Object);

        var entityType = ctx.Model.FindEntityType(typeof(PlainEntity));
        entityType.Should().NotBeNull();
        entityType!.FindDeclaredQueryFilter("SoftDelete").Should().BeNull();
    }

    // ── OnModelCreating: DataPrivilege ────────────────────────────────

    [Fact]
    public void OnModelCreating_WithEntityFilterContributor_RegistersNamedDataPrivilegeFilter()
    {
        using var ctx = new FilteredContext(
            NewInMemoryOptions<FilteredContext>(),
            CreateUser().Object,
            CreatePolicy().Object,
            [new TestEntityFilterContributor()],
            new Mock<IDynamicFilterProvider>().Object,
            new Mock<ITenantContext>().Object);

        var entityType = ctx.Model.FindEntityType(typeof(SoftDeletableWithFilter));
        entityType.Should().NotBeNull();
        entityType!.FindDeclaredQueryFilter("DataPrivilege:TestEntityFilterContributor")
            .Should().NotBeNull("Entity matching an IEntityFilterContributor must get a 'DataPrivilege:Xxx' named filter");
    }

    [Fact]
    public void OnModelCreating_NullBuildFilterResult_SkipsRegistration()
    {
        using var ctx = new EmptyContext(
            NewInMemoryOptions<EmptyContext>(),
            CreateUser().Object,
            CreatePolicy().Object,
            [new NoOpFilterContributor()],
            new Mock<IDynamicFilterProvider>().Object,
            new Mock<ITenantContext>().Object);

        var entityType = ctx.Model.FindEntityType(typeof(PlainEntity));
        entityType.Should().NotBeNull();
        entityType!.FindDeclaredQueryFilter("DataPrivilege:NoOpFilterContributor")
            .Should().BeNull("null BuildFilter result must not produce a filter");
    }

    [Fact]
    public void OnModelCreating_MultipleContributors_ForSameEntity_RegisterMultipleFilters()
    {
        using var ctx = new FilteredContext(
            NewInMemoryOptions<FilteredContext>(),
            CreateUser().Object,
            CreatePolicy().Object,
            [new TestEntityFilterContributor(), new AnotherContributor()],
            new Mock<IDynamicFilterProvider>().Object,
            new Mock<ITenantContext>().Object);

        var entityType = ctx.Model.FindEntityType(typeof(SoftDeletableWithFilter));
        entityType!.FindDeclaredQueryFilter("DataPrivilege:TestEntityFilterContributor").Should().NotBeNull();
        entityType.FindDeclaredQueryFilter("DataPrivilege:AnotherContributor").Should().NotBeNull();
    }

    // ── OnModelCreating: DynamicFilterProvider ────────────────────────

    [Fact]
    public void OnModelCreating_Invokes_DynamicFilterProvider_WithContext()
    {
        var mockProvider = new Mock<IDynamicFilterProvider>();
        using var ctx = new EmptyContext(
            NewInMemoryOptions<EmptyContext>(),
            CreateUser().Object,
            CreatePolicy().Object,
            [],
            mockProvider.Object,
            new Mock<ITenantContext>().Object);

        // Force model finalization
        _ = ctx.Model;

        mockProvider.Verify(
            p => p.ApplyDynamicFilters(It.IsAny<ModelBuilder>(), It.Is<BaseDataContext>(c => ReferenceEquals(c, ctx))),
            Times.Once,
            "DynamicFilterProvider.ApplyDynamicFilters must be called with the active context");
    }

    [Fact]
    public void OnModelCreating_DynamicFilterProvider_Invoked_AfterModelFinalization()
    {
        var mockProvider = new Mock<IDynamicFilterProvider>();
        var act = () =>
        {
            using var c = new EmptyContext(
                NewInMemoryOptions<EmptyContext>(),
                CreateUser().Object,
                CreatePolicy().Object,
                [],
                mockProvider.Object,
                new Mock<ITenantContext>().Object);
            c.PlainEntities.Add(new PlainEntity { Name = "x" });
            c.SaveChanges();
        };

        act.Should().NotThrow();
        mockProvider.Verify(
            p => p.ApplyDynamicFilters(It.IsAny<ModelBuilder>(), It.IsAny<BaseDataContext>()),
            Times.AtLeastOnce);
    }
}
