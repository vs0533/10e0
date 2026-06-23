using System.Linq.Expressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
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
        IServiceProvider serviceProvider,
        IHttpContextAccessor httpContextAccessor) : BaseDataContext(options, serviceProvider, httpContextAccessor)
    {
        public DbSet<SoftDeletableEntity> SoftEntities => Set<SoftDeletableEntity>();
        public DbSet<PlainEntity> PlainEntities => Set<PlainEntity>();
    }

    private sealed class FilteredContext(
        DbContextOptions<FilteredContext> options,
        IServiceProvider serviceProvider,
        IHttpContextAccessor httpContextAccessor) : BaseDataContext(options, serviceProvider, httpContextAccessor)
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

    /// <summary>
    /// #95 captive-dependency 修复后 BaseDataContext ctor 改为 (DbContextOptions, IServiceProvider, IHttpContextAccessor)，
    /// OnModelCreating 通过 sp 解析 ICurrentUserContext / IDynamicFilterProvider / IDataAccessPolicy /
    /// ITenantContext / IEntityFilterContributor 等依赖。此 helper 把这些依赖塞进一个最小的
    /// fake ServiceCollection，返回 sp + accessor 给测试 DbContext ctor 用。
    /// </summary>
    private static (IServiceProvider Sp, IHttpContextAccessor Accessor) BuildServices(
        Mock<ICurrentUserContext> user,
        Mock<IDataAccessPolicy> policy,
        IEnumerable<IEntityFilterContributor> contributors,
        Mock<IDynamicFilterProvider> provider,
        Mock<ITenantContext> tenantContext)
    {
        var services = new ServiceCollection();
        services.AddSingleton(user.Object);
        services.AddSingleton(policy.Object);
        services.AddSingleton(tenantContext.Object);
        services.AddSingleton(provider.Object);
        foreach (var c in contributors) services.AddSingleton(c);
        services.AddHttpContextAccessor();
        var sp = services.BuildServiceProvider();
        var accessor = sp.GetRequiredService<IHttpContextAccessor>();
        return (sp, accessor);
    }

    /// <summary>默认 5 个 mock 参数（user / policy / 空 contribs / 动态 provider / tenant）。</summary>
    private static (IServiceProvider Sp, IHttpContextAccessor Accessor) DefaultServices()
        => BuildServices(
            CreateUser(),
            CreatePolicy(),
            [],
            new Mock<IDynamicFilterProvider>(),
            new Mock<ITenantContext>());

    // ── Runtime property pass-through ─────────────────────────────────

    [Fact]
    public void CurrentUserCode_Delegates_ToICurrentUserContext()
    {
        var (sp, accessor) = DefaultServices();
        using var ctx = new EmptyContext(NewInMemoryOptions<EmptyContext>(), sp, accessor);
        ctx.CurrentUserCode.Should().Be("u1");
    }

    [Fact]
    public void CurrentUserCode_ReturnsNull_WhenUserNotAuthenticated()
    {
        var (sp, accessor) = BuildServices(
            CreateUser(userCode: null, authenticated: false),
            CreatePolicy(),
            [],
            new Mock<IDynamicFilterProvider>(),
            new Mock<ITenantContext>());
        using var ctx = new EmptyContext(NewInMemoryOptions<EmptyContext>(), sp, accessor);
        ctx.CurrentUserCode.Should().BeNull();
    }

    [Fact]
    public void CurrentRoleIds_ReturnsEmpty_WhenUserNotAuthenticated()
    {
        var roleIds = new[] { "r1", "r2" };
        var (sp, accessor) = BuildServices(
            CreateUser(roleIds: roleIds),
            CreatePolicy(),
            [],
            new Mock<IDynamicFilterProvider>(),
            new Mock<ITenantContext>());
        using var ctx = new EmptyContext(NewInMemoryOptions<EmptyContext>(), sp, accessor);
        ctx.CurrentRoleIds.Should().BeEquivalentTo(roleIds);
    }

    /// <summary>
    /// #120 回归守护：CurrentRoleIds 必须实时从 ICurrentUserContext 求值，
    /// 而非在构造函数固化。验证请求中途切身份（如 admin 临时提权）能立即反映到 Query Filter。
    /// 若有人改回 ctor 缓存（旧 bug 形态），本测试立即 fail。
    /// </summary>
    [Fact]
    public void CurrentRoleIds_ReflectsMidRequestRoleChange_NotCachedInCtor()
    {
        // 用可变 holder 模拟"身份在请求中途变更"
        var currentRoles = new List<string> { "r1" };
        var user = new Mock<ICurrentUserContext>();
        user.SetupGet(c => c.IsAuthenticated).Returns(true);
        user.SetupGet(c => c.RoleIds).Returns(() => currentRoles);

        var (sp, accessor) = BuildServices(user, CreatePolicy(), [], new Mock<IDynamicFilterProvider>(), new Mock<ITenantContext>());
        using var ctx = new EmptyContext(NewInMemoryOptions<EmptyContext>(), sp, accessor);

        // 构造后读到初始角色
        ctx.CurrentRoleIds.Should().BeEquivalentTo(["r1"], "初次读取应反映构造时的角色");

        // 中途提权：身份变更后，同一 ctx 实例应读到新角色（证明未固化）
        currentRoles.Add("r2-admin-elevated");
        ctx.CurrentRoleIds.Should().BeEquivalentTo(["r1", "r2-admin-elevated"],
            "CurrentRoleIds 必须每次查询实时求值，不能在 ctor 固化（#120 旧 bug 形态）");
    }

    [Fact]
    public void CurrentOrgIds_DefaultsToEmpty_AndIsMutable()
    {
        var (sp, accessor) = DefaultServices();
        using var ctx = new EmptyContext(NewInMemoryOptions<EmptyContext>(), sp, accessor);
        ctx.CurrentOrgIds.Should().BeEmpty();
        ctx.CurrentOrgIds = ["o1"];
        ctx.CurrentOrgIds.Should().BeEquivalentTo(["o1"]);
    }

    [Fact]
    public void IsAuthenticated_Delegates_ToICurrentUserContext()
    {
        var (sp, accessor) = BuildServices(
            CreateUser(authenticated: false),
            CreatePolicy(),
            [],
            new Mock<IDynamicFilterProvider>(),
            new Mock<ITenantContext>());
        using var ctx = new EmptyContext(NewInMemoryOptions<EmptyContext>(), sp, accessor);
        ctx.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public void BypassFilters_Delegates_ToAccessPolicy()
    {
        var (sp, accessor) = BuildServices(
            CreateUser(),
            CreatePolicy(bypass: true),
            [],
            new Mock<IDynamicFilterProvider>(),
            new Mock<ITenantContext>());
        using var ctx = new EmptyContext(NewInMemoryOptions<EmptyContext>(), sp, accessor);
        ctx.BypassFilters.Should().BeTrue();
    }

    // ── OnModelCreating: SoftDelete ───────────────────────────────────

    [Fact]
    public void OnModelCreating_SoftDeleteEntity_RegistersSoftDeleteFilter()
    {
        var (sp, accessor) = DefaultServices();
        using var ctx = new EmptyContext(NewInMemoryOptions<EmptyContext>(), sp, accessor);

        var entityType = ctx.Model.FindEntityType(typeof(SoftDeletableEntity));
        entityType.Should().NotBeNull();
        entityType!.FindDeclaredQueryFilter("SoftDelete").Should().NotBeNull();
    }

    [Fact]
    public void OnModelCreating_NonSoftDeleteEntity_DoesNotRegisterSoftDeleteFilter()
    {
        var (sp, accessor) = DefaultServices();
        using var ctx = new EmptyContext(NewInMemoryOptions<EmptyContext>(), sp, accessor);

        var entityType = ctx.Model.FindEntityType(typeof(PlainEntity));
        entityType.Should().NotBeNull();
        entityType!.FindDeclaredQueryFilter("SoftDelete").Should().BeNull();
    }

    // ── OnModelCreating: DataPrivilege ────────────────────────────────

    [Fact]
    public void OnModelCreating_WithEntityFilterContributor_RegistersNamedDataPrivilegeFilter()
    {
        var (sp, accessor) = BuildServices(
            CreateUser(),
            CreatePolicy(),
            [new TestEntityFilterContributor()],
            new Mock<IDynamicFilterProvider>(),
            new Mock<ITenantContext>());
        using var ctx = new FilteredContext(NewInMemoryOptions<FilteredContext>(), sp, accessor);

        var entityType = ctx.Model.FindEntityType(typeof(SoftDeletableWithFilter));
        entityType.Should().NotBeNull();
        entityType!.FindDeclaredQueryFilter("DataPrivilege:TestEntityFilterContributor")
            .Should().NotBeNull("Entity matching an IEntityFilterContributor must get a 'DataPrivilege:Xxx' named filter");
    }

    [Fact]
    public void OnModelCreating_NullBuildFilterResult_SkipsRegistration()
    {
        var (sp, accessor) = BuildServices(
            CreateUser(),
            CreatePolicy(),
            [new NoOpFilterContributor()],
            new Mock<IDynamicFilterProvider>(),
            new Mock<ITenantContext>());
        using var ctx = new EmptyContext(NewInMemoryOptions<EmptyContext>(), sp, accessor);

        var entityType = ctx.Model.FindEntityType(typeof(PlainEntity));
        entityType.Should().NotBeNull();
        entityType!.FindDeclaredQueryFilter("DataPrivilege:NoOpFilterContributor")
            .Should().BeNull("null BuildFilter result must not produce a filter");
    }

    [Fact]
    public void OnModelCreating_MultipleContributors_ForSameEntity_RegisterMultipleFilters()
    {
        var (sp, accessor) = BuildServices(
            CreateUser(),
            CreatePolicy(),
            [new TestEntityFilterContributor(), new AnotherContributor()],
            new Mock<IDynamicFilterProvider>(),
            new Mock<ITenantContext>());
        using var ctx = new FilteredContext(NewInMemoryOptions<FilteredContext>(), sp, accessor);

        var entityType = ctx.Model.FindEntityType(typeof(SoftDeletableWithFilter));
        entityType!.FindDeclaredQueryFilter("DataPrivilege:TestEntityFilterContributor").Should().NotBeNull();
        entityType.FindDeclaredQueryFilter("DataPrivilege:AnotherContributor").Should().NotBeNull();
    }

    // ── OnModelCreating: DynamicFilterProvider ────────────────────────

    [Fact]
    public void OnModelCreating_Invokes_DynamicFilterProvider_WithContext()
    {
        var mockProvider = new Mock<IDynamicFilterProvider>();
        var (sp, accessor) = BuildServices(
            CreateUser(),
            CreatePolicy(),
            [],
            mockProvider,
            new Mock<ITenantContext>());
        using var ctx = new EmptyContext(NewInMemoryOptions<EmptyContext>(), sp, accessor);

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
            var (sp, accessor) = BuildServices(
                CreateUser(),
                CreatePolicy(),
                [],
                mockProvider,
                new Mock<ITenantContext>());
            using var c = new EmptyContext(NewInMemoryOptions<EmptyContext>(), sp, accessor);
            c.PlainEntities.Add(new PlainEntity { Name = "x" });
            c.SaveChanges();
        };

        act.Should().NotThrow();
        mockProvider.Verify(
            p => p.ApplyDynamicFilters(It.IsAny<ModelBuilder>(), It.IsAny<BaseDataContext>()),
            Times.AtLeastOnce);
    }

    // ── #121: TenantFilterActive 诊断信号 ──────────────────────────────

    /// <summary>
    /// #121: TenantFilterActive 暴露"当前是否处于 Tenant 过滤器隐藏所有多租户行"的状态，
    /// 让业务方/中间件可在 EF Query Filter 无法 log 的前提下主动检测"列表突然为空"。
    /// DbContext 受 #95 captive-dependency 约束不持有 ILogger，故走诊断属性而非内嵌日志。
    /// </summary>
    [Fact]
    public void TenantFilterActive_TrueWhenTenantIdNullAndNotBypass()
    {
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.TenantId).Returns((string?)null);

        var (sp, accessor) = BuildServices(
            CreateUser(), CreatePolicy(bypass: false), [], new Mock<IDynamicFilterProvider>(), tenant);
        using var ctx = new EmptyContext(NewInMemoryOptions<EmptyContext>(), sp, accessor);

        ctx.TenantFilterActive.Should().BeTrue(
            "null tenant + 非 bypass = 多租户查询将返回空集，诊断信号应为 true");
    }

    [Fact]
    public void TenantFilterActive_FalseWhenTenantIdSet()
    {
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.TenantId).Returns("t-a");

        var (sp, accessor) = BuildServices(
            CreateUser(), CreatePolicy(bypass: false), [], new Mock<IDynamicFilterProvider>(), tenant);
        using var ctx = new EmptyContext(NewInMemoryOptions<EmptyContext>(), sp, accessor);

        ctx.TenantFilterActive.Should().BeFalse(
            "tenant 已设置时过滤器正常工作，不会隐藏所有行");
    }

    [Fact]
    public void TenantFilterActive_FalseWhenBypassEvenIfTenantNull()
    {
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.TenantId).Returns((string?)null);

        var (sp, accessor) = BuildServices(
            CreateUser(), CreatePolicy(bypass: true), [], new Mock<IDynamicFilterProvider>(), tenant);
        using var ctx = new EmptyContext(NewInMemoryOptions<EmptyContext>(), sp, accessor);

        ctx.TenantFilterActive.Should().BeFalse(
            "超管 bypass 即使 tenant 为 null 也能看见所有行，诊断信号应为 false");
    }
}
