using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TenE0.Core.Events.Outbox;
using TenE0.Core.Observability.HealthChecks;

namespace TenE0.Core.Tests.Observability.HealthChecks;

/// <summary>
/// #161 OutboxHealthCheck 阈值判定：积压数过 Degraded/Unhealthy 阈值返回对应状态；
/// 空 → Healthy；毒消息（AttemptCount >= MaxAttempts）不计入积压。
/// </summary>
[Trait("Category", "Unit")]
public sealed class OutboxHealthCheckTests
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

    private static IDbContextFactory<TestDbContext> CreateFactory(string dbName)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TestDbContextFactory(options);
    }

    private static OutboxMessage Pending(int attempt = 0) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        EventType = "test",
        Payload = "{}",
        OccurredOn = DateTimeOffset.UtcNow,
        SentTime = null,
        AttemptCount = attempt,
    };

    private static OutboxHealthCheck<TestDbContext> CreateCheck(
        IDbContextFactory<TestDbContext> factory,
        int degraded = 100,
        int unhealthy = 1000,
        int maxAttempts = 8)
        => new(
            factory,
            Options.Create(new TenE0.Core.Observability.ObservabilityOptions
            {
                OutboxDegradedThreshold = degraded,
                OutboxUnhealthyThreshold = unhealthy,
            }),
            Options.Create(new OutboxRelayOptions { MaxAttempts = maxAttempts }));

    private static async Task SeedAsync(string dbName, params OutboxMessage[] messages)
    {
        var factory = CreateFactory(dbName);
        await using var ctx = factory.CreateDbContext();
        ctx.OutboxMessages.AddRange(messages);
        await ctx.SaveChangesAsync();
    }

    private static HealthCheckContext EmptyContext => new();

    [Fact]
    public async Task CheckHealthAsync_NoBacklog_ReturnsHealthy()
    {
        var dbName = $"obs-outbox-empty-{Guid.NewGuid():N}";
        var check = CreateCheck(CreateFactory(dbName));

        var result = await check.CheckHealthAsync(EmptyContext);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealthAsync_BacklogAboveDegraded_ReturnsDegraded()
    {
        var dbName = $"obs-outbox-degraded-{Guid.NewGuid():N}";
        // 恰好达到 Degraded 阈值（>= 5）。
        await SeedAsync(dbName, Enumerable.Range(0, 5).Select(_ => Pending()).ToArray());
        var check = CreateCheck(CreateFactory(dbName), degraded: 5, unhealthy: 100);

        var result = await check.CheckHealthAsync(EmptyContext);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["backlog"].Should().Be(5);
    }

    [Fact]
    public async Task CheckHealthAsync_BacklogAboveUnhealthy_ReturnsUnhealthy()
    {
        var dbName = $"obs-outbox-unhealthy-{Guid.NewGuid():N}";
        await SeedAsync(dbName, Enumerable.Range(0, 10).Select(_ => Pending()).ToArray());
        var check = CreateCheck(CreateFactory(dbName), degraded: 1, unhealthy: 10);

        var result = await check.CheckHealthAsync(EmptyContext);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data["backlog"].Should().Be(10);
    }

    [Fact]
    public async Task CheckHealthAsync_PoisonMessages_NotCountedInBacklog()
    {
        var dbName = $"obs-outbox-poison-{Guid.NewGuid():N}";
        // 3 条正常待发 + 2 条毒消息（AttemptCount 已达 MaxAttempts=8）。
        await SeedAsync(dbName,
            Enumerable.Range(0, 3).Select(_ => Pending()).Append(Pending(attempt: 8)).Append(Pending(attempt: 10)).ToArray());
        var check = CreateCheck(CreateFactory(dbName), degraded: 100, unhealthy: 1000, maxAttempts: 8);

        var result = await check.CheckHealthAsync(EmptyContext);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["backlog"].Should().Be(3, "毒消息（AttemptCount >= MaxAttempts）不计入积压");
    }
}
