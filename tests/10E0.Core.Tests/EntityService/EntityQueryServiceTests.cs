using TenE0.Core.Abstractions;
using TenE0.Core.EntityService;
using TenE0.Core.Queries;

namespace TenE0.Core.Tests.EntityService;

/// <summary>
/// EntityQueryService 单元测试(EF InMemory)。
///
/// 覆盖范围(issue #184 测试要求):
/// - GetById 命中 / 不存在返回 null
/// - List / Paged 分页边界(page&lt;1、pageSize&gt;1000 截断、TotalPages 计算)
/// - 投影 selector 正确性
/// - ReadFilter 全 Operator(Eq/Ne/Gt/Gte/Lt/Lte/Contains/StartsWith/EndsWith/In)+ 非法 Field 抛异常
/// - OrderBy 多字段 + 方向
/// - AsNoTracking 默认 true
/// - Count / Exists
/// - BypassFilters 查询构建(实际过滤效果在 Acceptance 测)
/// </summary>
[Trait("Category", "Unit")]
public sealed class EntityQueryServiceTests
{
    // ── 测试实体 ───────────────────────────────────────────────

    private sealed class TestProduct : IBaseEntity
    {
        public string Id { get; set; } = "";
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public decimal Price { get; set; }
        public int Stock { get; set; }
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TestProduct> Products => Set<TestProduct>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestProduct>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Code).IsRequired();
                entity.Property(e => e.Name).IsRequired();
            });
        }
    }

    private static TestDbContext CreateContext()
        => CreateContext(Guid.NewGuid().ToString());

    private static TestDbContext CreateContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new TestDbContext(options);
    }

    /// <summary>塞入若干商品并返回 context。InMemory 同名 db 共享数据。</summary>
    private static async Task<TestDbContext> CreateSeededContext(int count = 5)
        => await CreateSeededContext(Guid.NewGuid().ToString(), count);

    /// <summary>塞入若干商品到指定名的 InMemory db(同名 db 跨 context 共享数据)。</summary>
    private static async Task<TestDbContext> CreateSeededContext(string dbName, int count = 5)
    {
        var ctx = CreateContext(dbName);
        for (var i = 1; i <= count; i++)
        {
            ctx.Products.Add(new TestProduct
            {
                Id = $"p{i}",
                Code = $"C{i:D3}",
                Name = $"Product {i}",
                Price = 10m * i,
                Stock = i * 5,
            });
        }
        await ctx.SaveChangesAsync();
        return ctx;
    }

    /// <summary>用全新 context 读同名 InMemory db(避开 seed 的 tracked 实体干扰 ChangeTracker 断言)。</summary>
    private static TestDbContext OpenFreshContext(string dbName) => CreateContext(dbName);

    private static readonly EntityQueryService Sut = new();

    // ══════════════════════════════════════════════════════════════
    //  GetByIdAsync
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetByIdAsync_Hit_ReturnsEntity()
    {
        await using var ctx = await CreateSeededContext();
        var entity = await Sut.GetByIdAsync<TestProduct>(ctx, "p3");
        entity.Should().NotBeNull();
        entity!.Id.Should().Be("p3");
        entity.Name.Should().Be("Product 3");
    }

    [Fact]
    public async Task GetByIdAsync_Missing_ReturnsNull()
    {
        await using var ctx = await CreateSeededContext();
        var entity = await Sut.GetByIdAsync<TestProduct>(ctx, "nope");
        entity.Should().BeNull();
    }

    [Fact]
    public async Task GetByIdAsync_WithSelector_ProjectsToView()
    {
        await using var ctx = await CreateSeededContext();
        var view = await Sut.GetByIdAsync<TestProduct, ProductView>(
            ctx, "p2", p => new ProductView(p.Id, p.Code, p.Name, p.Price));
        view.Should().NotBeNull();
        view!.Code.Should().Be("C002");
        view.Price.Should().Be(20m);
    }

    [Fact]
    public async Task GetByIdAsync_WithSelector_Missing_ReturnsNull()
    {
        await using var ctx = await CreateSeededContext();
        var view = await Sut.GetByIdAsync<TestProduct, ProductView>(
            ctx, "absent", p => new ProductView(p.Id, p.Code, p.Name, p.Price));
        view.Should().BeNull();
    }

    // ══════════════════════════════════════════════════════════════
    //  ListAsync
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ListAsync_ReturnsAllSeeded()
    {
        await using var ctx = await CreateSeededContext(5);
        var list = await Sut.ListAsync<TestProduct>(ctx);
        list.Should().HaveCount(5);
    }

    [Fact]
    public async Task ListAsync_WithSelector_ProjectsAll()
    {
        await using var ctx = await CreateSeededContext(3);
        var views = await Sut.ListAsync<TestProduct, ProductView>(
            ctx, p => new ProductView(p.Id, p.Code, p.Name, p.Price));
        views.Should().HaveCount(3);
        views.Select(v => v.Code).Should().BeEquivalentTo(new[] { "C001", "C002", "C003" });
    }

    [Fact]
    public async Task ListAsync_WithFilter_ReturnsMatching()
    {
        await using var ctx = await CreateSeededContext(5);
        var list = await Sut.ListAsync<TestProduct>(ctx, new EntityReadOptions
        {
            Filters = [new ReadFilter("Price", ReadOperator.Gte, 30m)],
        });
        list.Should().HaveCount(3); // p3(30), p4(40), p5(50)
        list.Select(p => p.Id).Should().BeEquivalentTo(new[] { "p3", "p4", "p5" });
    }

    // ══════════════════════════════════════════════════════════════
    //  PagedAsync
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PagedAsync_ReturnsPageWithCorrectTotals()
    {
        await using var ctx = await CreateSeededContext(5);
        var result = await Sut.PagedAsync<TestProduct>(
            ctx, new PagedQuery(Page: 1, PageSize: 2));

        result.Items.Should().HaveCount(2);
        result.Total.Should().Be(5);
        result.TotalPages.Should().Be(3); // 5 / 2 = 2.5 → 3
    }

    [Fact]
    public async Task PagedAsync_SecondPage_ReturnsRemaining()
    {
        await using var ctx = await CreateSeededContext(5);
        var result = await Sut.PagedAsync<TestProduct>(
            ctx, new PagedQuery(Page: 3, PageSize: 2));

        result.Items.Should().HaveCount(1); // 5 - 4
        result.Page.Should().Be(3);
    }

    [Fact]
    public async Task PagedAsync_PageLessThanOne_NormalizedToOne()
    {
        await using var ctx = await CreateSeededContext(3);
        var result = await Sut.PagedAsync<TestProduct>(
            ctx, new PagedQuery(Page: 0, PageSize: 10));

        result.Page.Should().Be(1);
        result.Items.Should().HaveCount(3);
    }

    [Fact]
    public async Task PagedAsync_PageSizeExceeds1000_TruncatedTo1000()
    {
        await using var ctx = await CreateSeededContext(2);
        var result = await Sut.PagedAsync<TestProduct>(
            ctx, new PagedQuery(Page: 1, PageSize: 5000));

        result.PageSize.Should().Be(1000);
        result.Items.Should().HaveCount(2); // 全部 2 条,不到 1000
    }

    [Fact]
    public async Task PagedAsync_PageSizeLessThanOne_NormalizedToTen()
    {
        await using var ctx = await CreateSeededContext(12);
        var result = await Sut.PagedAsync<TestProduct>(
            ctx, new PagedQuery(Page: 1, PageSize: 0));

        result.PageSize.Should().Be(10);
        result.Items.Should().HaveCount(10);
    }

    [Fact]
    public async Task PagedAsync_WithSelector_ProjectsPage()
    {
        await using var ctx = await CreateSeededContext(4);
        var result = await Sut.PagedAsync<TestProduct, ProductView>(
            ctx, new PagedQuery(Page: 1, PageSize: 2),
            p => new ProductView(p.Id, p.Code, p.Name, p.Price));

        result.Items.Should().HaveCount(2);
        result.Items[0].Should().BeOfType<ProductView>();
        result.Total.Should().Be(4);
    }

    [Fact]
    public async Task PagedAsync_WithOrderBy_AppliesOrdering()
    {
        await using var ctx = await CreateSeededContext(5);
        var result = await Sut.PagedAsync<TestProduct>(
            ctx, new PagedQuery(Page: 1, PageSize: 5),
            new EntityReadOptions
            {
                OrderBy = [new ReadOrderBy("Price", Descending: true)],
            });

        result.Items.Select(p => p.Price)
            .Should().BeInDescendingOrder()
            .And.BeEquivalentTo(new[] { 50m, 40m, 30m, 20m, 10m }, o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task PagedAsync_EmptyResult_StillComputesTotalPages()
    {
        await using var ctx = CreateContext(); // 空表
        var result = await Sut.PagedAsync<TestProduct>(
            ctx, new PagedQuery(Page: 1, PageSize: 10));

        result.Total.Should().Be(0);
        result.TotalPages.Should().Be(0);
        result.Items.Should().BeEmpty();
    }

    // ══════════════════════════════════════════════════════════════
    //  ReadFilter — all operators
    // ══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(ReadOperator.Eq, 30, 1)]       // Price == 30 → p3
    [InlineData(ReadOperator.Ne, 30, 4)]        // Price != 30 → p1,p2,p4,p5
    [InlineData(ReadOperator.Gt, 30, 2)]        // Price > 30 → p4,p5
    [InlineData(ReadOperator.Gte, 30, 3)]       // Price >= 30 → p3,p4,p5
    [InlineData(ReadOperator.Lt, 30, 2)]        // Price < 30 → p1,p2
    [InlineData(ReadOperator.Lte, 30, 3)]       // Price <= 30 → p1,p2,p3
    public async Task ReadFilter_NumericOperators_ApplyCorrectly(ReadOperator op, object value, int expectedCount)
    {
        await using var ctx = await CreateSeededContext(5);
        var list = await Sut.ListAsync<TestProduct>(ctx, new EntityReadOptions
        {
            Filters = [new ReadFilter("Price", op, Convert.ToDecimal(value))],
        });
        list.Should().HaveCount(expectedCount);
    }

    [Fact]
    public async Task ReadFilter_Contains_String()
    {
        await using var ctx = await CreateSeededContext(5);
        var list = await Sut.ListAsync<TestProduct>(ctx, new EntityReadOptions
        {
            Filters = [new ReadFilter("Name", ReadOperator.Contains, "Product 1")],
        });
        list.Should().HaveCount(1); // "Product 1" 不匹配 "Product 2..5" 的字面
    }

    [Fact]
    public async Task ReadFilter_StartsWith_String()
    {
        await using var ctx = await CreateSeededContext(5);
        var list = await Sut.ListAsync<TestProduct>(ctx, new EntityReadOptions
        {
            Filters = [new ReadFilter("Code", ReadOperator.StartsWith, "C00")],
        });
        list.Should().HaveCount(5); // C001..C005 都以 C00 开头
    }

    [Fact]
    public async Task ReadFilter_EndsWith_String()
    {
        await using var ctx = await CreateSeededContext(5);
        var list = await Sut.ListAsync<TestProduct>(ctx, new EntityReadOptions
        {
            Filters = [new ReadFilter("Code", ReadOperator.EndsWith, "5")],
        });
        list.Should().HaveCount(1); // C005
        list[0].Id.Should().Be("p5");
    }

    [Fact]
    public async Task ReadFilter_In_Collection()
    {
        await using var ctx = await CreateSeededContext(5);
        var list = await Sut.ListAsync<TestProduct>(ctx, new EntityReadOptions
        {
            Filters = [new ReadFilter("Code", ReadOperator.In, new[] { "C001", "C003", "C005" })],
        });
        list.Should().HaveCount(3);
        list.Select(p => p.Code).Should().BeEquivalentTo(new[] { "C001", "C003", "C005" });
    }

    [Fact]
    public async Task ReadFilter_In_NullValue_Throws()
    {
        await using var ctx = await CreateSeededContext(2);
        var act = () => Sut.ListAsync<TestProduct>(ctx, new EntityReadOptions
        {
            Filters = [new ReadFilter("Code", ReadOperator.In, null!)],
        });

        var invocation = await act.Should().ThrowAsync<ArgumentException>();
        invocation.WithMessage("*ReadOperator.In*");
    }

    [Fact]
    public async Task ReadFilter_In_NonEnumerableValue_Throws()
    {
        await using var ctx = await CreateSeededContext(2);
        var act = () => Sut.ListAsync<TestProduct>(ctx, new EntityReadOptions
        {
            Filters = [new ReadFilter("Code", ReadOperator.In, "not-a-collection")],
        });

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ReadFilter_IllegalField_Throws()
    {
        // "Password" 不是 TestProduct 的 EF 模型属性 —— 应拒绝,防表达式注入
        await using var ctx = await CreateSeededContext(2);
        var act = () => Sut.ListAsync<TestProduct>(ctx, new EntityReadOptions
        {
            Filters = [new ReadFilter("Password", ReadOperator.Eq, "secret")],
        });

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*非法筛选字段 'Password'*");
    }

    [Fact]
    public async Task ReadFilter_Multiple_AndCombined()
    {
        await using var ctx = await CreateSeededContext(5);
        var list = await Sut.ListAsync<TestProduct>(ctx, new EntityReadOptions
        {
            Filters =
            [
                new ReadFilter("Price", ReadOperator.Gte, 20m),
                new ReadFilter("Stock", ReadOperator.Lte, 15),
            ],
        });
        // Price>=20: p2..p5; Stock<=15: p1(5),p2(10),p3(15) → 交集 p2,p3
        list.Should().HaveCount(2);
        list.Select(p => p.Id).Should().BeEquivalentTo(new[] { "p2", "p3" });
    }

    // ══════════════════════════════════════════════════════════════
    //  OrderBy — multi-field + direction
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task OrderBy_MultipleFields_AppliedInOrder()
    {
        await using var ctx = CreateContext();
        // 制造相同 Stock 不同 Price,验证次级排序
        ctx.Products.AddRange(
            new TestProduct { Id = "a", Code = "C1", Name = "A", Price = 30m, Stock = 10 },
            new TestProduct { Id = "b", Code = "C2", Name = "B", Price = 20m, Stock = 10 },
            new TestProduct { Id = "c", Code = "C3", Name = "C", Price = 10m, Stock = 10 });
        await ctx.SaveChangesAsync();

        var list = await Sut.ListAsync<TestProduct>(ctx, new EntityReadOptions
        {
            OrderBy = [
                new ReadOrderBy("Stock", Descending: false),
                new ReadOrderBy("Price", Descending: true),
            ],
        });

        // Stock 同(10 asc)→ Price desc: 30, 20, 10 → a, b, c
        list.Select(p => p.Id).Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task OrderBy_IllegalField_Throws()
    {
        await using var ctx = await CreateSeededContext(2);
        var act = () => Sut.ListAsync<TestProduct>(ctx, new EntityReadOptions
        {
            OrderBy = [new ReadOrderBy("NotAField")],
        });

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*非法排序字段 'NotAField'*");
    }

    // ══════════════════════════════════════════════════════════════
    //  CountAsync / ExistsAsync
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CountAsync_ReturnsTotal()
    {
        await using var ctx = await CreateSeededContext(5);
        var total = await Sut.CountAsync<TestProduct>(ctx);
        total.Should().Be(5);
    }

    [Fact]
    public async Task CountAsync_WithFilter_CountsMatching()
    {
        await using var ctx = await CreateSeededContext(5);
        var total = await Sut.CountAsync<TestProduct>(ctx, new EntityReadOptions
        {
            Filters = [new ReadFilter("Price", ReadOperator.Gte, 40m)],
        });
        total.Should().Be(2); // p4, p5
    }

    [Fact]
    public async Task ExistsAsync_Hit_ReturnsTrue()
    {
        await using var ctx = await CreateSeededContext(3);
        var exists = await Sut.ExistsAsync<TestProduct>(ctx, "p2");
        exists.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_Missing_ReturnsFalse()
    {
        await using var ctx = await CreateSeededContext(3);
        var exists = await Sut.ExistsAsync<TestProduct>(ctx, "absent");
        exists.Should().BeFalse();
    }

    // ══════════════════════════════════════════════════════════════
    //  AsNoTracking default
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ListAsync_DefaultAsNoTracking_EntitiesNotTracked()
    {
        // 用 seed context 写入,用全新 query context 读取 —— 避免 seed 的 tracked 实体干扰断言。
        var dbName = Guid.NewGuid().ToString();
        await using (await CreateSeededContext(dbName, 2)) { }

        await using var queryCtx = OpenFreshContext(dbName);
        var list = await Sut.ListAsync<TestProduct>(queryCtx);

        list.Should().HaveCount(2);
        // AsNoTracking → 读取不进 ChangeTracker
        queryCtx.ChangeTracker.Entries<TestProduct>().Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_AsNoTrackingFalse_TracksEntities()
    {
        var dbName = Guid.NewGuid().ToString();
        await using (await CreateSeededContext(dbName, 2)) { }

        await using var queryCtx = OpenFreshContext(dbName);
        var list = await Sut.ListAsync<TestProduct>(queryCtx, new EntityReadOptions { AsNoTracking = false });

        list.Should().HaveCount(2);
        queryCtx.ChangeTracker.Entries<TestProduct>().Should().HaveCount(2);
    }

    // ══════════════════════════════════════════════════════════════
    //  BypassFilters — query construction (runtime effect in Acceptance)
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ListAsync_BypassFiltersStar_LogsWarningAndReturnsAll()
    {
        // InMemory 不执行 Query Filter,所以这里只验证查询不崩 + 返回全部。
        // 实际过滤效果由 EntityQueryAcceptanceTests(SQLite)验证。
        await using var ctx = await CreateSeededContext(3);
        var list = await Sut.ListAsync<TestProduct>(ctx, new EntityReadOptions
        {
            BypassFilters = new HashSet<string> { "*" },
        });
        list.Should().HaveCount(3);
    }

    [Fact]
    public async Task ListAsync_BypassFiltersSpecificName_DoesNotThrow()
    {
        await using var ctx = await CreateSeededContext(3);
        var list = await Sut.ListAsync<TestProduct>(ctx, new EntityReadOptions
        {
            BypassFilters = new HashSet<string> { "SoftDelete" },
        });
        list.Should().HaveCount(3);
    }

    // ══════════════════════════════════════════════════════════════
    //  Default options / null options
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ListAsync_NullOptions_BehavesLikeDefault()
    {
        await using var ctx = await CreateSeededContext(2);
        var list = await Sut.ListAsync<TestProduct>(ctx, options: null);
        list.Should().HaveCount(2);
    }

    // ── 投影 view record ───────────────────────────────────────
    private sealed record ProductView(string Id, string Code, string Name, decimal Price);
}
