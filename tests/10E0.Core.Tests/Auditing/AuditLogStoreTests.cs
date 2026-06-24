using TenE0.Core.Auditing;

namespace TenE0.Core.Tests.Auditing;

/// <summary>
/// <see cref="AuditLogStore{TContext}"/> 单元测试 — 分页 + 多过滤条件组合查询（issue #152 §8）。
/// </summary>
[Trait("Category", "Unit")]
public sealed class AuditLogStoreTests
{
    private sealed class TestDbContext : DbContext
    {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ConfigureTenE0AuditTables();
        }
    }

    private sealed class TestFactory(DbContextOptions<TestDbContext> options) : IDbContextFactory<TestDbContext>
    {
        public TestDbContext CreateDbContext() => new(options);
    }

    private static IDbContextFactory<TestDbContext> CreateFactory(string dbName)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new TestFactory(options);
    }

    private static async Task SeedAsync(string dbName, params TenE0AuditLog[] logs)
    {
        var f = CreateFactory(dbName);
        await using var dc = f.CreateDbContext();
        dc.Set<TenE0AuditLog>().AddRange(logs);
        await dc.SaveChangesAsync();
    }

    private static TenE0AuditLog Op(
        string actor, string type, string id, string action, DateTimeOffset time) => new()
        {
            ActorCode = actor,
            EntityType = type,
            EntityId = id,
            Action = action,
            ChangedFieldsJson = "[]",
            CreateTime = time,
        };

    [Fact]
    public async Task QueryAsync_AppliesActorAndActionFilter()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var t = new DateTimeOffset(2026, 6, 24, 10, 0, 0, TimeSpan.Zero);
        await SeedAsync(dbName,
            Op("alice", "Order", "1", "Create", t),
            Op("bob", "Order", "2", "Create", t.AddMinutes(1)),
            Op("alice", "Order", "1", "Update", t.AddMinutes(2)));

        var factory = CreateFactory(dbName);
        var store = new AuditLogStore<TestDbContext>(factory);

        var result = await store.QueryAsync(new AuditLogQuery { ActorCode = "alice" });

        result.Total.Should().Be(2);
        result.Items.Should().OnlyContain(a => a.ActorCode == "alice");
    }

    [Fact]
    public async Task QueryAsync_AppliesEntityTypeAndEntityIdFilter()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var t = DateTimeOffset.UtcNow;
        await SeedAsync(dbName,
            Op("alice", "Order", "1", "Create", t),
            Op("alice", "Order", "2", "Create", t.AddMinutes(1)),
            Op("alice", "Product", "1", "Create", t.AddMinutes(2)));

        var factory = CreateFactory(dbName);
        var store = new AuditLogStore<TestDbContext>(factory);

        var result = await store.QueryAsync(
            new AuditLogQuery { EntityType = "Order", EntityId = "1" });

        result.Total.Should().Be(1);
        result.Items.Single().EntityId.Should().Be("1");
    }

    [Fact]
    public async Task QueryAsync_OrdersByCreateTimeDescendingAndPaginates()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var baseTime = new DateTimeOffset(2026, 6, 24, 0, 0, 0, TimeSpan.Zero);
        var logs = Enumerable.Range(0, 5)
            .Select(i => Op("u", "T", i.ToString(), "Create", baseTime.AddMinutes(i)))
            .ToArray();
        await SeedAsync(dbName, logs);

        var factory = CreateFactory(dbName);
        var store = new AuditLogStore<TestDbContext>(factory);

        // 第 1 页 size=2 → 最新 2 条（i=4, i=3）
        var page1 = await store.QueryAsync(new AuditLogQuery { Page = 1, Size = 2 });
        page1.Total.Should().Be(5);
        page1.Page.Should().Be(1);
        page1.Size.Should().Be(2);
        page1.Items.Select(i => i.EntityId)
            .Should().Equal(["4", "3"], "倒序，最新在前");

        // 第 2 页 size=2 → i=2, i=1
        var page2 = await store.QueryAsync(new AuditLogQuery { Page = 2, Size = 2 });
        page2.Items.Select(i => i.EntityId).Should().Equal(["2", "1"]);
    }

    [Fact]
    public async Task QueryAsync_AppliesTimeRangeFilter()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var t = new DateTimeOffset(2026, 6, 24, 10, 0, 0, TimeSpan.Zero);
        await SeedAsync(dbName,
            Op("u", "T", "1", "Create", t),
            Op("u", "T", "2", "Create", t.AddHours(1)),
            Op("u", "T", "3", "Create", t.AddHours(5)));

        var factory = CreateFactory(dbName);
        var store = new AuditLogStore<TestDbContext>(factory);

        var result = await store.QueryAsync(
            new AuditLogQuery { From = t.AddMinutes(30), To = t.AddHours(2) });

        result.Total.Should().Be(1);
        result.Items.Single().EntityId.Should().Be("2");
    }

    [Fact]
    public async Task QueryAsync_NormalizesInvalidPaging()
    {
        var dbName = Guid.NewGuid().ToString("N");
        await SeedAsync(dbName, Op("u", "T", "1", "Create", DateTimeOffset.UtcNow));

        var factory = CreateFactory(dbName);
        var store = new AuditLogStore<TestDbContext>(factory);

        var result = await store.QueryAsync(new AuditLogQuery { Page = -5, Size = 99999 });

        result.Page.Should().Be(1, "Page<1 规整为 1");
        result.Size.Should().Be(200, "Size>200 上限为 200");
    }

    [Fact]
    public async Task QueryLoginsAsync_FiltersByUserAndSuccess()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        await using (var dc = factory.CreateDbContext())
        {
            dc.Set<TenE0LoginLog>().AddRange(
                new TenE0LoginLog { UserCode = "alice", EventType = "Login", Success = true, CreateTime = DateTimeOffset.UtcNow },
                new TenE0LoginLog { UserCode = "alice", EventType = "Failed", Success = false, CreateTime = DateTimeOffset.UtcNow },
                new TenE0LoginLog { UserCode = "bob", EventType = "Login", Success = true, CreateTime = DateTimeOffset.UtcNow });
            await dc.SaveChangesAsync();
        }

        var store = new AuditLogStore<TestDbContext>(factory);

        var aliceFailed = await store.QueryLoginsAsync(
            new LoginLogQuery { UserCode = "alice", Success = false });
        aliceFailed.Total.Should().Be(1);
        aliceFailed.Items.Single().EventType.Should().Be("Failed");

        var allAlice = await store.QueryLoginsAsync(new LoginLogQuery { UserCode = "alice" });
        allAlice.Total.Should().Be(2);
    }
}
