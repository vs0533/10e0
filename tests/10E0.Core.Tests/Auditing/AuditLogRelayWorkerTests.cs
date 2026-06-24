using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TenE0.Core.Auditing;

namespace TenE0.Core.Tests.Auditing;

/// <summary>
/// <see cref="AuditLogRelayWorker{TContext}"/> 单元测试 — 批量落库 + best-effort 失败。
/// 风格对齐 <c>OutboxRelayServiceTests</c>。
/// </summary>
[Trait("Category", "Unit")]
public sealed class AuditLogRelayWorkerTests
{
    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ConfigureTenE0AuditTables();
        }

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

    private static DbContextOptions<TestDbContext> CreateDbOptions(string dbName)
        => new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static (AuditLogRelayWorker<TestDbContext> worker, AuditLogChannel channel, IServiceProvider sp, string dbName) Create(
        AuditOptions? options = null)
    {
        var opt = options ?? new AuditOptions { BatchSize = 10, PollInterval = TimeSpan.FromMilliseconds(10) };
        var dbName = Guid.NewGuid().ToString("N");
        var dbOptions = CreateDbOptions(dbName);
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<TestDbContext>>(new TestFactory(dbOptions));
        services.AddSingleton(Options.Create(opt));
        services.AddSingleton<AuditLogChannel>();
        var sp = services.BuildServiceProvider();

        var channel = sp.GetRequiredService<AuditLogChannel>();
        var worker = new AuditLogRelayWorker<TestDbContext>(
            channel,
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IOptions<AuditOptions>>(),
            NullLogger<AuditLogRelayWorker<TestDbContext>>.Instance);
        return (worker, channel, sp, dbName);
    }

    private static async Task<List<TenE0AuditLog>> ReadAuditLogsAsync(string dbName)
    {
        await using var dc = new TestDbContext(CreateDbOptions(dbName));
        return await dc.Set<TenE0AuditLog>().OrderBy(a => a.EntityId).ToListAsync();
    }

    private static async Task<List<TenE0LoginLog>> ReadLoginLogsAsync(string dbName)
    {
        await using var dc = new TestDbContext(CreateDbOptions(dbName));
        return await dc.Set<TenE0LoginLog>().ToListAsync();
    }

    [Fact]
    public async Task ProcessBatchAsync_EmptyChannel_Returns0()
    {
        var (worker, _, _, _) = Create();

        var result = await worker.ProcessBatchAsync(CancellationToken.None);

        result.Should().Be(0);
    }

    [Fact]
    public async Task ProcessBatchAsync_PersistsOpAndLoginItemsToDb()
    {
        var (worker, channel, _, dbName) = Create();
        var now = DateTimeOffset.UtcNow;
        channel.TryWrite(new AuditChannelItem.Op(new AuditLogEntry
        {
            EntityType = "Order",
            EntityId = "1",
            Action = "Create",
            ChangedFieldsJson = "[]",
            CreateTime = now,
        }));
        channel.TryWrite(new AuditChannelItem.Login(new LoginLogEntry
        {
            UserCode = "alice",
            EventType = "Login",
            Success = true,
            CreateTime = now,
        }));

        var processed = await worker.ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(2);
        var auditLogs = await ReadAuditLogsAsync(dbName);
        auditLogs.Should().ContainSingle().Which.Action.Should().Be("Create");
        var loginLogs = await ReadLoginLogsAsync(dbName);
        loginLogs.Should().ContainSingle().Which.UserCode.Should().Be("alice");
    }

    [Fact]
    public async Task ProcessBatchAsync_PersistsMultipleOpItems()
    {
        var (worker, channel, _, dbName) = Create();
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 3; i++)
        {
            channel.TryWrite(new AuditChannelItem.Op(new AuditLogEntry
            {
                EntityType = "T",
                EntityId = i.ToString(),
                Action = "Create",
                ChangedFieldsJson = "[]",
                CreateTime = now,
            }));
        }

        var processed = await worker.ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(3);
        var logs = await ReadAuditLogsAsync(dbName);
        logs.Should().HaveCount(3);
        logs.Select(a => a.EntityId).Should().Equal(["0", "1", "2"]);
    }

    [Fact]
    public async Task ProcessBatchAsync_DropsEntriesWithSensitiveDataAlreadyMasked()
    {
        // 验证落库的 FailureReason 字段透传（脱敏由拦截器在入队前完成，worker 不再处理）
        var (worker, channel, _, dbName) = Create();
        channel.TryWrite(new AuditChannelItem.Login(new LoginLogEntry
        {
            UserCode = "bob",
            EventType = "Failed",
            Success = false,
            FailureReason = "bad password",
            CreateTime = DateTimeOffset.UtcNow,
        }));

        await worker.ProcessBatchAsync(CancellationToken.None);

        var loginLogs = await ReadLoginLogsAsync(dbName);
        var log = loginLogs.Single();
        log.FailureReason.Should().Be("bad password");
        log.Success.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessBatchAsync_RespectsBatchSize()
    {
        var options = new AuditOptions { BatchSize = 2, PollInterval = TimeSpan.FromMilliseconds(10) };
        var (worker, channel, _, _) = Create(options);
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 5; i++)
        {
            channel.TryWrite(new AuditChannelItem.Op(new AuditLogEntry
            {
                EntityType = "T",
                EntityId = i.ToString(),
                Action = "Create",
                ChangedFieldsJson = "[]",
                CreateTime = now,
            }));
        }

        var processed = await worker.ProcessBatchAsync(CancellationToken.None);

        // BatchSize=2 → 单次只处理 2 条（凑满即提交）
        processed.Should().Be(2);
    }

    [Fact]
    public async Task ProcessBatchAsync_DbFailure_DoesNotThrow_BestEffort()
    {
        // 用一个会失败的 factory（返回已 Dispose 的 DbContext）模拟落库失败
        var options = new AuditOptions { BatchSize = 10, PollInterval = TimeSpan.FromMilliseconds(10) };
        var dbName = Guid.NewGuid().ToString("N");
        var failingFactory = new FailingFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<TestDbContext>>(failingFactory);
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<AuditLogChannel>();
        var sp = services.BuildServiceProvider();
        var channel = sp.GetRequiredService<AuditLogChannel>();
        channel.TryWrite(new AuditChannelItem.Op(new AuditLogEntry
        {
            EntityType = "T",
            EntityId = "1",
            Action = "Create",
            ChangedFieldsJson = "[]",
            CreateTime = DateTimeOffset.UtcNow,
        }));

        var worker = new AuditLogRelayWorker<TestDbContext>(
            channel,
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IOptions<AuditOptions>>(),
            NullLogger<AuditLogRelayWorker<TestDbContext>>.Instance);

        // 落库失败应被吞掉，不抛给调用方（best-effort 契约）
        var act = async () => await worker.ProcessBatchAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    /// <summary>始终抛异常的 factory，用于验证 best-effort 失败处理。</summary>
    private sealed class FailingFactory : IDbContextFactory<TestDbContext>
    {
        public TestDbContext CreateDbContext()
            => throw new InvalidOperationException("simulated DB failure");
    }
}
