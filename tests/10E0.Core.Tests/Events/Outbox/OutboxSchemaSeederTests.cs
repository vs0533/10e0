using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.DependencyInjection;
using TenE0.Core.Events.Outbox;
using TenE0.Core.Hosting;

namespace TenE0.Core.Tests.Events.Outbox;

/// <summary>
/// 单元测试 — OutboxSchemaSeeder 的注册路径与短路行为。
///
/// <para>
/// 为什么不写 SQLite 端到端测试？
/// 当前 Seeder 列存在性探测覆盖 SqlServer / Postgres；SQLite 会落到
/// <c>"SELECT 0"</c> 分支 → 强制执行 ADD COLUMN，触发 SQLite <c>ALTER TABLE</c>
/// 无 <c>IF NOT EXISTS</c> 报 duplicate column name。
/// 真实联调由集成测试（#80 follow-up）覆盖；本测试聚焦：
/// 1) 非关系型 provider（如 InMemory）必须短路返回，不抛异常；
/// 2) <c>AddTenE0DomainEvents</c> 必须把 Seeder 注册到 DI 容器。
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class OutboxSchemaSeederTests
{
    public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ConfigureTenE0OutboxTables();
        }
    }

    [Fact]
    public async Task GivenInMemoryProvider_WhenSeedAsync_ThenShortCircuitsWithoutThrowing()
    {
        // Arrange — InMemory provider 不是 relational，Seeder 必须短路返回
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var ctx = new TestDbContext(options);
        var sut = new OutboxSchemaSeeder();

        // Act + Assert — 任何异常都算 bug（短路失败）
        Func<Task> act = () => sut.SeedAsync(ctx, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void GivenOutboxSchemaSeeder_WhenInstantiated_ThenOrderIsZero()
    {
        // Arrange + Act
        var sut = new OutboxSchemaSeeder();

        // Then — Order=0 保证先于任何业务 Seeder（业务 Seeder 通常 Order=10+）
        sut.Order.Should().Be(0, "Order=0 是 Seeder 在 OutboxMessage 升级后第一个跑的关键约定");
    }

    [Fact]
    public void GivenServiceCollection_WhenAddTenE0DomainEventsCalled_ThenOutboxSchemaSeederRegistered()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddLogging()
            .AddEntityFrameworkInMemoryDatabase();
        services.AddDbContextFactory<TestDbContext>((_, o) => o.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));

        // Act — 走 AddTenE0DomainEvents 默认注册路径
        services.AddTenE0DomainEvents<TestDbContext>();

        // Assert — Seeder 必须被注册到 IDataSeeder 集合
        using var sp = services.BuildServiceProvider();
        var seeders = sp.GetServices<IDataSeeder>().ToList();

        seeders.Should().Contain(s => s is OutboxSchemaSeeder,
            "AddTenE0DomainEvents 必须把 OutboxSchemaSeeder 注入 DI 容器，"
            + "否则 DatabaseInitializerService 启动时不会调用，#80 的 schema 升级会完全失效");
    }

    [Fact]
    public void GivenServiceCollection_WhenAddTenE0DomainEventsCalled_ThenIOutboxLockRegisteredAsNoOp()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddLogging()
            .AddEntityFrameworkInMemoryDatabase();
        services.AddDbContextFactory<TestDbContext>((_, o) => o.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));

        // Act
        services.AddTenE0DomainEvents<TestDbContext>();

        // Assert — 多实例部署零感知：默认必须是 NoOp
        using var sp = services.BuildServiceProvider();
        var lockObj = sp.GetService<IOutboxLock>();

        lockObj.Should().NotBeNull();
        lockObj.Should().BeOfType<NoOpOutboxLock>(
            "默认 NoOp — 多实例部署应当通过 services.Replace 切到具体 provider 实现");
    }

    [Fact]
    public void GivenAddTenE0DomainEvents_WhenCalledTwice_ThenSeederNotDuplicated()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddLogging()
            .AddEntityFrameworkInMemoryDatabase();
        services.AddDbContextFactory<TestDbContext>((_, o) => o.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));

        // Act — 重复注册（业务方可能不小心在多个位置调）
        services.AddTenE0DomainEvents<TestDbContext>();
        services.AddTenE0DomainEvents<TestDbContext>();

        // Assert — TryAddEnumerable 保证幂等
        using var sp = services.BuildServiceProvider();
        var seeders = sp.GetServices<IDataSeeder>().OfType<OutboxSchemaSeeder>().ToList();

        seeders.Should().HaveCount(1,
            "TryAddEnumerable 必须保证重复 AddTenE0DomainEvents 不会让 Seeder 被注册多次（Order=0 × N = 跑 N 次）");
    }
}
