using Microsoft.EntityFrameworkCore.Diagnostics;
using TenE0.Core.Workflow.Definitions;

namespace TenE0.Core.Tests.Workflow.Definitions;

/// <summary>
/// #158 ProcessDefinitionStore 测试 — 版本管理 + 查询。
/// </summary>
[Trait("Category", "Unit")]
public sealed class ProcessDefinitionStoreTests
{
    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TenE0ProcessDefinition> ProcessDefinitions => Set<TenE0ProcessDefinition>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ConfigureTenE0WorkflowDefinitionTables();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            optionsBuilder.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        }
    }

    private sealed class TestFactory(DbContextOptions<TestDbContext> options) : IDbContextFactory<TestDbContext>
    {
        public TestDbContext CreateDbContext() => new(options);
    }

    private static (ProcessDefinitionStore<TestDbContext> store, TestFactory factory) CreateStore(string dbName)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var factory = new TestFactory(options);
        return (new ProcessDefinitionStore<TestDbContext>(factory), factory);
    }

    private static TenE0ProcessDefinition MakeDef(string code, string nodesJson = "[]")
        => new()
        {
            Code = code,
            Name = code,
            NodesJson = nodesJson,
            StartNodeCode = "start",
            TenantId = "t1",
        };

    [Fact]
    public async Task PublishAsync_FirstVersion_IsLatestAndVersion1()
    {
        var (store, _) = CreateStore(Guid.NewGuid().ToString("N"));

        var def = await store.PublishAsync(MakeDef("expense"));

        def.Version.Should().Be(1);
        def.IsLatest.Should().BeTrue();
    }

    [Fact]
    public async Task PublishAsync_SecondVersion_DemotesPreviousAndBumpsVersion()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var (store1, _) = CreateStore(dbName);
        var v1 = await store1.PublishAsync(MakeDef("expense"));

        // 新 factory 但同一 InMemory DB 名（共享数据）
        var (store2, _) = CreateStore(dbName);
        var v2 = await store2.PublishAsync(MakeDef("expense"));

        v2.Version.Should().Be(2);
        v2.IsLatest.Should().BeTrue();

        // 验证 v1 的 IsLatest 被置 false
        var fetchedV1 = await store2.GetAsync("expense", 1);
        fetchedV1!.IsLatest.Should().BeFalse("发布 v2 时旧版本应被降级");
    }

    [Fact]
    public async Task GetLatestAsync_ReturnsLatestVersion()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var (store1, _) = CreateStore(dbName);
        await store1.PublishAsync(MakeDef("expense"));
        var (store2, _) = CreateStore(dbName);
        await store2.PublishAsync(MakeDef("expense"));

        var (store3, _) = CreateStore(dbName);
        var latest = await store3.GetLatestAsync("expense");

        latest!.Version.Should().Be(2);
    }

    [Fact]
    public async Task ListVersionsAsync_ReturnsDescendingByVersion()
    {
        var dbName = Guid.NewGuid().ToString("N");
        for (var i = 0; i < 3; i++)
        {
            var (s, _) = CreateStore(dbName);
            await s.PublishAsync(MakeDef("expense"));
        }

        var (store, _) = CreateStore(dbName);
        var versions = await store.ListVersionsAsync("expense");

        versions.Should().HaveCount(3);
        versions.Select(v => v.Version).Should().BeEquivalentTo([3, 2, 1], opts => opts.WithStrictOrdering());
    }

    [Fact]
    public async Task DisableAsync_SetsIsEnabledFalse()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var (store1, _) = CreateStore(dbName);
        var def = await store1.PublishAsync(MakeDef("expense"));

        var (store2, _) = CreateStore(dbName);
        await store2.DisableAsync(def.Id);

        var (store3, _) = CreateStore(dbName);
        var fetched = await store3.GetByIdAsync(def.Id);
        fetched!.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task GetAsync_UnknownCode_ReturnsNull()
    {
        var (store, _) = CreateStore(Guid.NewGuid().ToString("N"));

        var result = await store.GetAsync("nonexistent", 1);

        result.Should().BeNull();
    }
}
