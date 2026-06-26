using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TenE0.Core.Events.Outbox;
using TenE0.Core.Observability.HealthChecks;

namespace TenE0.Core.Tests.Observability.HealthChecks;

/// <summary>
/// #161 DbContextHealthCheck：可达 → Healthy；探测失败 → Unhealthy。
/// </summary>
[Trait("Category", "Unit")]
public sealed class DbContextHealthCheckTests
{
    public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ConfigureTenE0OutboxTables();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            optionsBuilder.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<TestDbContext> options)
        : IDbContextFactory<TestDbContext>
    {
        public TestDbContext CreateDbContext() => new(options);
    }

    /// <summary>
    /// 一个总是抛异常的工厂 —— 模拟数据库不可达（连接失败 / provider 错误）。
    /// </summary>
    private sealed class ThrowingDbContextFactory : IDbContextFactory<TestDbContext>
    {
        public TestDbContext CreateDbContext() =>
            throw new InvalidOperationException("数据库连接失败（模拟）");
    }

    private static HealthCheckContext EmptyContext => new();

    [Fact]
    public async Task CheckHealthAsync_Reachable_ReturnsHealthy()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase($"obs-db-ok-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var factory = new TestDbContextFactory(options);
        var check = new DbContextHealthCheck<TestDbContext>(factory);

        var result = await check.CheckHealthAsync(EmptyContext);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealthAsync_Unreachable_ReturnsUnhealthy()
    {
        var check = new DbContextHealthCheck<TestDbContext>(new ThrowingDbContextFactory());

        var result = await check.CheckHealthAsync(EmptyContext);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().BeOfType<InvalidOperationException>();
    }
}
