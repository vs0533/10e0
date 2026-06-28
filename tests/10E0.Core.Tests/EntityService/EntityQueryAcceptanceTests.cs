using System.Linq.Expressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.Abstractions;
using TenE0.Core.DataContext;
using TenE0.Core.DynamicFilters;
using TenE0.Core.EntityService;
using TenE0.Core.Permissions.DataFilter;
using TenE0.Core.Queries;

namespace TenE0.Core.Tests.EntityService;

/// <summary>
/// EntityQueryService BDD 集成测试(SQLite in-memory,真执行 Named Query Filter)。
///
/// EF InMemory provider 不强制 HasQueryFilter,所以行级/软删/租户过滤的实际效果必须用
/// 关系型 provider(SQLite)验证。结构对齐 <c>TenantQueryFilterAcceptanceTests</c>。
///
/// 验收点(issue #184):
/// 1. 软删除自动过滤 —— GetById 软删行返回 null
/// 2. 租户隔离 —— A 用户查不到 B 数据
/// 3. 行级权限(IEntityFilterContributor) —— 过滤生效
/// 4. BypassFilters=["Tenant"] —— 细粒度旁路(可见跨租户,但仍排软删)
/// 5. BypassFilters=["*"] —— 全量旁路(可见软删 + 跨租户)
/// 6. 默认(无 BypassFilters) —— 全部过滤器生效
/// 7. 超管(BypassFilters=true via IDataAccessPolicy) —— 短路
/// </summary>
[Trait("Category", "BDD")]
public sealed class EntityQueryAcceptanceTests
{
    // ── 测试实体 ───────────────────────────────────────────────

    /// <summary>同时实现软删除 + 多租户 + 行级权限的复合实体,覆盖三种过滤器共存。</summary>
    private sealed class AssetDoc : IMultiTenantEntity, ISoftDeleteEntity
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string TenantId { get; set; } = string.Empty;
        public string OwnerCode { get; set; } = string.Empty;  // 行级权限按 owner 过滤
        public string Title { get; set; } = string.Empty;
        public bool IsSoftDelete { get; set; }
        public DateTimeOffset? DeleteTime { get; set; }
        public string? DeleteBy { get; set; }
    }

    /// <summary>行级权限贡献者:只允许看到 OwnerCode == 当前用户 的行(超管除外)。</summary>
    private sealed class OwnerRowFilterContributor : EntityFilterContributor<AssetDoc>
    {
        protected override Expression<Func<AssetDoc, bool>>? Build(BaseDataContext context)
        {
            // BypassFilters || e.OwnerCode == dc.CurrentUserCode
            return e => context.BypassFilters || e.OwnerCode == context.CurrentUserCode;
        }
    }

    private sealed class TestAssetDbContext(
        DbContextOptions<TestAssetDbContext> options,
        IServiceProvider serviceProvider,
        IHttpContextAccessor httpContextAccessor) : BaseDataContext(options, serviceProvider, httpContextAccessor)
    {
        public DbSet<AssetDoc> Docs => Set<AssetDoc>();
    }

    // ── DI / SQLite 基础设施(复刻 TenantQueryFilterAcceptanceTests) ─────────
    // 注:SQLite options 不挂 IModelCacheKeyFactory —— 模型在上下文间共享缓存。
    // 这要求 seed context 与 query context 注册**相同**的 contributor 集合,否则
    // 首次模型构建(在 seed context 上)不会注册 contributor 的命名过滤器。
    // (EnsureCreated 的 migration differ 在带 per-instance 模型缓存键时崩溃,
    // 故此处跟随 TenantQueryFilterAcceptanceTests 用共享缓存方案。)

    private static Mock<ICurrentUserContext> CreateUser(string code)
    {
        var mock = new Mock<ICurrentUserContext>();
        mock.SetupGet(c => c.IsAuthenticated).Returns(true);
        mock.SetupGet(c => c.UserCode).Returns(code);
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

    private static (IServiceProvider Sp, IHttpContextAccessor Accessor) BuildServices(
        Mock<ICurrentUserContext> user,
        Mock<IDataAccessPolicy> policy,
        IEnumerable<IEntityFilterContributor> contributors,
        Mock<IDynamicFilterProvider> dynamicProvider,
        Mock<ITenantContext> tenant)
    {
        var services = new ServiceCollection();
        services.AddSingleton(user.Object);
        services.AddSingleton(policy.Object);
        services.AddSingleton(tenant.Object);
        services.AddSingleton(dynamicProvider.Object);
        foreach (var c in contributors) services.AddSingleton(c);
        services.AddHttpContextAccessor();
        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredService<IHttpContextAccessor>());
    }

    private sealed class SqliteRuntimeHandle(SqliteConnection connection) : IDisposable
    {
        public DbContextOptions<TestAssetDbContext> Options { get; } =
            new DbContextOptionsBuilder<TestAssetDbContext>()
                .UseSqlite(connection)
                .Options;
        public void Dispose()
        {
            try { connection.Close(); } catch { /* noop */ }
            connection.Dispose();
        }
    }

    private static SqliteRuntimeHandle NewSqlite()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return new SqliteRuntimeHandle(connection);
    }

    /// <summary>种子:跨租户 + 跨 owner + 软删的完整矩阵。</summary>
    /// <remarks>
    /// 必须把 <see cref="OwnerRowFilterContributor"/> 也注册到 seed context,否则
    /// 首次模型构建(在 seed context 上发生)不会注册 DataPrivilege 命名过滤器,
    /// 而 SQLite 模型在上下文间共享缓存 → 后续查询上下文的 contributor 永远生效不了。
    /// (与 Tenant 测试不同:后者没有行级 contributor,只需 tenant context 一致即可。)
    /// </remarks>
    private static async Task SeedAsync(SqliteRuntimeHandle sqlite)
    {
        var tenantA = CreateTenantContext("t-a");
        var (sp, accessor) = BuildServices(
            CreateUser("seeder"), CreatePolicy(bypass: true), [new OwnerRowFilterContributor()],
            CreateDynamicProvider(), tenantA);
        using var seed = new TestAssetDbContext(sqlite.Options, sp, accessor);
        await seed.Database.EnsureDeletedAsync();
        await seed.Database.EnsureCreatedAsync();

        seed.Docs.AddRange(
            // 租户 A,owner alice
            new AssetDoc { Id = "a1", TenantId = "t-a", OwnerCode = "alice", Title = "A-alice-1" },
            new AssetDoc { Id = "a2", TenantId = "t-a", OwnerCode = "bob", Title = "A-bob-1" },
            // 租户 B,owner alice
            new AssetDoc { Id = "b1", TenantId = "t-b", OwnerCode = "alice", Title = "B-alice-1" },
            // 软删除(alice 自己的,租户 A)
            new AssetDoc { Id = "del1", TenantId = "t-a", OwnerCode = "alice", Title = "deleted", IsSoftDelete = true });
        await seed.SaveChangesAsync();
    }

    // ══════════════════════════════════════════════════════════════
    //  软删除自动过滤
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GivenRowIsSoftDeleted_WhenGetById_ThenReturnsNull()
    {
        using var sqlite = NewSqlite();
        await SeedAsync(sqlite);

        var (sp, accessor) = BuildServices(
            CreateUser("alice"), CreatePolicy(), [new OwnerRowFilterContributor()],
            CreateDynamicProvider(), CreateTenantContext("t-a"));
        await using var ctx = new TestAssetDbContext(sqlite.Options, sp, accessor);
        var sut = new EntityQueryService();

        var doc = await sut.GetByIdAsync<AssetDoc>(ctx, "del1");

        // 软删 + 非超管 → 软删过滤器把行隐藏,等同"不存在"
        doc.Should().BeNull();
    }

    [Fact]
    public async Task GivenRowIsSoftDeleted_WhenList_ThenExcluded()
    {
        using var sqlite = NewSqlite();
        await SeedAsync(sqlite);

        var (sp, accessor) = BuildServices(
            CreateUser("alice"), CreatePolicy(), [new OwnerRowFilterContributor()],
            CreateDynamicProvider(), CreateTenantContext("t-a"));
        await using var ctx = new TestAssetDbContext(sqlite.Options, sp, accessor);
        var sut = new EntityQueryService();

        var list = await sut.ListAsync<AssetDoc>(ctx);

        // alice(t-a)应只看到 a1,del1(软删)和 a2/b1(owner/tenant 都不符)排除
        list.Should().ContainSingle().Which.Id.Should().Be("a1");
    }

    // ══════════════════════════════════════════════════════════════
    //  租户隔离
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GivenTenantAUser_WhenList_ThenTenantBRowsAreHidden()
    {
        using var sqlite = NewSqlite();
        await SeedAsync(sqlite);

        var (sp, accessor) = BuildServices(
            CreateUser("alice"), CreatePolicy(), [new OwnerRowFilterContributor()],
            CreateDynamicProvider(), CreateTenantContext("t-a"));
        await using var ctx = new TestAssetDbContext(sqlite.Options, sp, accessor);
        var sut = new EntityQueryService();

        var ids = await sut.ListAsync<AssetDoc, string>(ctx, d => d.Id);

        // alice(t-a)只看到 a1(b1 是 t-b,即便 owner 是 alice 也隐藏)
        ids.Should().Equal("a1");
    }

    [Fact]
    public async Task GivenTenantBUser_WhenList_ThenSeesOnlyTenantB()
    {
        using var sqlite = NewSqlite();
        await SeedAsync(sqlite);

        var (sp, accessor) = BuildServices(
            CreateUser("alice"), CreatePolicy(), [new OwnerRowFilterContributor()],
            CreateDynamicProvider(), CreateTenantContext("t-b"));
        await using var ctx = new TestAssetDbContext(sqlite.Options, sp, accessor);
        var sut = new EntityQueryService();

        var ids = await sut.ListAsync<AssetDoc, string>(ctx, d => d.Id);

        ids.Should().Equal("b1");
    }

    // ══════════════════════════════════════════════════════════════
    //  行级权限(IEntityFilterContributor)
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GivenOwnerRowFilter_WhenList_ThenOnlyOwnRows()
    {
        using var sqlite = NewSqlite();
        await SeedAsync(sqlite);

        // bob(t-a):应该看到 a2(owner=bob,租户=t-a),看不到 a1(owner=alice)
        var (sp, accessor) = BuildServices(
            CreateUser("bob"), CreatePolicy(), [new OwnerRowFilterContributor()],
            CreateDynamicProvider(), CreateTenantContext("t-a"));
        await using var ctx = new TestAssetDbContext(sqlite.Options, sp, accessor);
        var sut = new EntityQueryService();

        var ids = await sut.ListAsync<AssetDoc, string>(ctx, d => d.Id);

        ids.Should().Equal("a2");
    }

    // ══════════════════════════════════════════════════════════════
    //  BypassFilters —— 三态
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GivenDefaultOptions_WhenList_ThenAllFiltersApply()
    {
        using var sqlite = NewSqlite();
        await SeedAsync(sqlite);

        var (sp, accessor) = BuildServices(
            CreateUser("alice"), CreatePolicy(), [new OwnerRowFilterContributor()],
            CreateDynamicProvider(), CreateTenantContext("t-a"));
        await using var ctx = new TestAssetDbContext(sqlite.Options, sp, accessor);
        var sut = new EntityQueryService();

        // 默认(无 BypassFilters):软删 + 租户 + owner 全应用
        var list = await sut.ListAsync<AssetDoc>(ctx);

        list.Should().ContainSingle().Which.Id.Should().Be("a1");
    }

    [Fact]
    public async Task GivenBypassFiltersStar_WhenList_ThenAllFiltersBypassed()
    {
        using var sqlite = NewSqlite();
        await SeedAsync(sqlite);

        var (sp, accessor) = BuildServices(
            CreateUser("alice"), CreatePolicy(), [new OwnerRowFilterContributor()],
            CreateDynamicProvider(), CreateTenantContext("t-a"));
        await using var ctx = new TestAssetDbContext(sqlite.Options, sp, accessor);
        var sut = new EntityQueryService();

        // ["*"] 全量旁路 —— 应见全部 4 条(含 del1 软删、b1 跨租户、a2 跨 owner)
        var list = await sut.ListAsync<AssetDoc>(ctx, new EntityReadOptions
        {
            BypassFilters = new HashSet<string> { "*" },
        });

        list.Should().HaveCount(4);
        list.Select(d => d.Id).Should().BeEquivalentTo(new[] { "a1", "a2", "b1", "del1" });
    }

    [Fact]
    public async Task GivenBypassFiltersTenant_WhenList_ThenOnlyTenantBypassed()
    {
        using var sqlite = NewSqlite();
        await SeedAsync(sqlite);

        var (sp, accessor) = BuildServices(
            CreateUser("alice"), CreatePolicy(), [new OwnerRowFilterContributor()],
            CreateDynamicProvider(), CreateTenantContext("t-a"));
        await using var ctx = new TestAssetDbContext(sqlite.Options, sp, accessor);
        var sut = new EntityQueryService();

        // ["Tenant"] 细粒度旁路:租户过滤失效(可见 b1),但软删 + owner 仍生效
        var list = await sut.ListAsync<AssetDoc>(ctx, new EntityReadOptions
        {
            BypassFilters = new HashSet<string> { "Tenant" },
        });

        // alice 的行跨租户:a1(t-a) + b1(t-b);del1 软删仍排除;a2 owner=bob 仍排除
        var ids = list.Select(d => d.Id).OrderBy(x => x).ToList();
        ids.Should().Equal("a1", "b1");
    }

    [Fact]
    public async Task GivenBypassFiltersSoftDelete_WhenGetByIdSoftDeleted_ThenReturnsRow()
    {
        using var sqlite = NewSqlite();
        await SeedAsync(sqlite);

        var (sp, accessor) = BuildServices(
            CreateUser("alice"), CreatePolicy(), [new OwnerRowFilterContributor()],
            CreateDynamicProvider(), CreateTenantContext("t-a"));
        await using var ctx = new TestAssetDbContext(sqlite.Options, sp, accessor);
        var sut = new EntityQueryService();

        // ["SoftDelete"] 细粒度旁路:软删可见,但 owner + tenant 仍生效
        var doc = await sut.GetByIdAsync<AssetDoc>(ctx, "del1", new EntityReadOptions
        {
            BypassFilters = new HashSet<string> { "SoftDelete" },
        });

        // del1:租户 t-a + owner alice 都符 → 仅软删旁路后可见
        doc.Should().NotBeNull();
        doc!.Id.Should().Be("del1");
    }

    // ══════════════════════════════════════════════════════════════
    //  超管短路(IDataAccessPolicy.BypassFilters=true)
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GivenSuperAdmin_WhenList_ThenAllRowsVisibleWithoutExplicitBypass()
    {
        using var sqlite = NewSqlite();
        await SeedAsync(sqlite);

        // 超管:IDataAccessPolicy.BypassFilters=true,表达式内 || context.BypassFilters 短路
        // 注意:SoftDelete 和 Tenant 过滤器在 BaseDataContext 里也 OR 上 BypassFilters
        var (sp, accessor) = BuildServices(
            CreateUser("admin"), CreatePolicy(bypass: true), [new OwnerRowFilterContributor()],
            CreateDynamicProvider(), CreateTenantContext("t-a"));
        await using var ctx = new TestAssetDbContext(sqlite.Options, sp, accessor);
        var sut = new EntityQueryService();

        // 不传 BypassFilters:超管靠 policy 自动短路,但 del1(软删)的 SoftDelete 过滤器表达式
        // 本身没 OR BypassFilters(BaseDataContext 只在 Tenant/owner 表达式里 OR),
        // 故软删仍排除 → 应见 3 条(a1, a2, b1)
        var list = await sut.ListAsync<AssetDoc>(ctx);

        var ids = list.Select(d => d.Id).OrderBy(x => x).ToList();
        ids.Should().Equal("a1", "a2", "b1");
    }

    // ══════════════════════════════════════════════════════════════
    //  投影 + 过滤共存(列表页标准用法)
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GivenFiltersAndProjection_WhenPaged_ThenReturnsProjectedPage()
    {
        using var sqlite = NewSqlite();
        await SeedAsync(sqlite);

        var (sp, accessor) = BuildServices(
            CreateUser("alice"), CreatePolicy(bypass: true), [new OwnerRowFilterContributor()],
            CreateDynamicProvider(), CreateTenantContext("t-a"));
        await using var ctx = new TestAssetDbContext(sqlite.Options, sp, accessor);
        var sut = new EntityQueryService();

        // 投影 + Title Contains 过滤 + 排序 + 分页(超管视角)
        var result = await sut.PagedAsync<AssetDoc, AssetView>(
            ctx,
            query: new PagedQuery(Page: 1, PageSize: 10),
            selector: d => new AssetView(d.Id, d.Title),
            options: new EntityReadOptions
            {
                Filters = [new ReadFilter("Title", ReadOperator.Contains, "A")],
                OrderBy = [new ReadOrderBy("Title", Descending: false)],
            });

        result.Total.Should().Be(2); // a1(A-alice-1), a2(A-bob-1) 含 "A"
        result.Items.Should().HaveCount(2);
        result.Items[0].Title.Should().Be("A-alice-1");
        result.Items[1].Title.Should().Be("A-bob-1");
    }

    private sealed record AssetView(string Id, string Title);
}
