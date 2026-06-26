using System.Reflection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TenE0.Core.Events.Outbox;

namespace TenE0.Core.Tests.Events.Outbox;

[Trait("Category", "Unit")]
public sealed class OutboxRelayServiceTests
{
    // ================================================================
    // Test Infrastructure
    // ================================================================

    public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ConfigureTenE0OutboxTables();
        }

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

    private static async Task SeedMessagesAsync(string dbName, params OutboxMessage[] messages)
    {
        var factory = CreateFactory(dbName);
        await using var ctx = factory.CreateDbContext();
        ctx.OutboxMessages.AddRange(messages);
        await ctx.SaveChangesAsync();
    }

    private static (OutboxRelayService<TestDbContext> Service, Mock<IOutboxPublisher> Publisher, Mock<IOutboxLock> Lock) CreateService(
        string dbName,
        Action<OutboxRelayOptions>? configureOptions = null,
        TimeProvider? timeProvider = null,
        Mock<IOutboxLock>? lockMock = null)
    {
        var options = new OutboxRelayOptions();
        configureOptions?.Invoke(options);
        var factory = CreateFactory(dbName);
        var publisherMock = new Mock<IOutboxPublisher>();
        // 当调用方未传 lockMock 时，新建一个默认 "TryAcquire=true" 的 mock
        // （与 NoOpOutboxLock 等价，不影响既有测试）。如果调用方已传 lockMock，
        // 不要重复 Setup —— Moq 中"后一个相同表达式的 Setup"会覆盖前一个，
        // 导致调用方传入的 ReturnsAsync(false) 被本默认 Setup 覆盖（参见
        // ProcessBatchAsync_WhenLockSkips_AttemptCountNotIncremented 的历史教训）。
        var effectiveLockMock = lockMock ?? new Mock<IOutboxLock>();
        if (lockMock is null)
        {
            effectiveLockMock
                .Setup(l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        }

        var serviceProviderMock = new Mock<IServiceProvider>();
        serviceProviderMock
            .Setup(sp => sp.GetService(typeof(IDbContextFactory<TestDbContext>)))
            .Returns(factory);
        serviceProviderMock
            .Setup(sp => sp.GetService(typeof(IOutboxPublisher)))
            .Returns(publisherMock.Object);
        serviceProviderMock
            .Setup(sp => sp.GetService(typeof(IOutboxLock)))
            .Returns(effectiveLockMock.Object);

        var scopeMock = new Mock<IServiceScope>();
        scopeMock.SetupGet(s => s.ServiceProvider).Returns(serviceProviderMock.Object);

        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

        var logger = NullLogger<OutboxRelayService<TestDbContext>>.Instance;
        var tp = timeProvider ?? TimeProvider.System;

        var service = new OutboxRelayService<TestDbContext>(
            scopeFactoryMock.Object,
            Options.Create(options),
            tp,
            logger);

        return (service, publisherMock, effectiveLockMock);
    }

    private static Task<int> InvokeProcessBatchAsync(OutboxRelayService<TestDbContext> service, CancellationToken ct = default)
    {
        var method = typeof(OutboxRelayService<TestDbContext>)
            .GetMethod("ProcessBatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)
                     ?? throw new InvalidOperationException("ProcessBatchAsync not found");
        return (Task<int>)method.Invoke(service, [ct])!;
    }

    private static Task InvokeExecuteAsync(OutboxRelayService<TestDbContext> service, CancellationToken ct)
    {
        var method = typeof(OutboxRelayService<TestDbContext>)
            .GetMethod("ExecuteAsync", BindingFlags.NonPublic | BindingFlags.Instance)
                     ?? throw new InvalidOperationException("ExecuteAsync not found");
        return (Task)method.Invoke(service, [ct])!;
    }

    // ================================================================
    // ProcessBatchAsync Tests
    // ================================================================

    [Fact]
    public async Task ProcessBatchAsync_NoMessages_Returns0()
    {
        var (service, _, _) = CreateService(Guid.NewGuid().ToString("N"));

        var result = await InvokeProcessBatchAsync(service);

        result.Should().Be(0);
    }

    [Fact]
    public async Task ProcessBatchAsync_AllSucceed_SetsSentTime()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString("N");
        var tp = new FakeTimeProvider();
        var now = tp.GetUtcNow();
        var msgs = new[]
        {
            new OutboxMessage { EventType = "E1", Payload = "{}", OccurredOn = DateTimeOffset.UtcNow.AddMinutes(-2) },
            new OutboxMessage { EventType = "E2", Payload = "{}", OccurredOn = DateTimeOffset.UtcNow.AddMinutes(-1) },
            new OutboxMessage { EventType = "E3", Payload = "{}", OccurredOn = DateTimeOffset.UtcNow },
        };
        await SeedMessagesAsync(dbName, msgs);
        var (service, pubMock, _) = CreateService(dbName, o => o.BatchSize = 10, tp);

        pubMock
            .Setup(p => p.PublishAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await InvokeProcessBatchAsync(service);

        // Assert
        result.Should().Be(3);

        var factory = CreateFactory(dbName);
        await using var ctx = factory.CreateDbContext();
        var saved = await ctx.OutboxMessages.OrderBy(m => m.OccurredOn).ToListAsync();
        saved.Should().HaveCount(3);
        saved.Should().AllSatisfy(m =>
        {
            m.SentTime.Should().Be(now);
            m.LastError.Should().BeNull();
            m.AttemptCount.Should().Be(1);
        });
    }

    [Fact]
    public async Task ProcessBatchAsync_PartialFailure_IncrementsAttempt()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString("N");
        var msgs = new[]
        {
            new OutboxMessage { EventType = "Good", Payload = "{}", OccurredOn = DateTimeOffset.UtcNow.AddMinutes(-1) },
            new OutboxMessage { EventType = "Bad", Payload = "{}", OccurredOn = DateTimeOffset.UtcNow },
        };
        await SeedMessagesAsync(dbName, msgs);
        var (service, pubMock, _) = CreateService(dbName, o => o.BatchSize = 10);

        pubMock
            .Setup(p => p.PublishAsync(It.Is<OutboxMessage>(m => m.EventType == "Good"), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        pubMock
            .Setup(p => p.PublishAsync(It.Is<OutboxMessage>(m => m.EventType == "Bad"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("network error"));

        // Act
        var result = await InvokeProcessBatchAsync(service);

        // Assert
        result.Should().Be(2);

        var factory = CreateFactory(dbName);
        await using var ctx = factory.CreateDbContext();
        var saved = await ctx.OutboxMessages.OrderBy(m => m.OccurredOn).ToListAsync();

        saved[0].SentTime.Should().NotBeNull();
        saved[0].LastError.Should().BeNull();
        saved[0].AttemptCount.Should().Be(1);

        saved[1].SentTime.Should().BeNull();
        saved[1].LastError.Should().Be("network error");
        saved[1].AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task ProcessBatchAsync_ExceedsMaxAttempts_SkipsMessage()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString("N");
        var fresh = new OutboxMessage { EventType = "Fresh", Payload = "{}", OccurredOn = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var poison = new OutboxMessage { EventType = "Poison", Payload = "{}", OccurredOn = DateTimeOffset.UtcNow, AttemptCount = 3 };
        await SeedMessagesAsync(dbName, fresh, poison);
        var (service, pubMock, _) = CreateService(dbName, o =>
        {
            o.BatchSize = 10;
            o.MaxAttempts = 3;
        });

        pubMock
            .Setup(p => p.PublishAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await InvokeProcessBatchAsync(service);

        // Assert
        result.Should().Be(1);
        pubMock.Verify(
            p => p.PublishAsync(It.Is<OutboxMessage>(m => m.EventType == "Poison"), It.IsAny<CancellationToken>()),
            Times.Never);

        var factory = CreateFactory(dbName);
        await using var ctx = factory.CreateDbContext();
        var poisonMsg = await ctx.OutboxMessages.SingleAsync(m => m.EventType == "Poison");
        poisonMsg.AttemptCount.Should().Be(3);
        poisonMsg.SentTime.Should().BeNull();
    }

    [Fact]
    public async Task ProcessBatchAsync_TruncatesLongError()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString("N");
        var msg = new OutboxMessage { EventType = "Err", Payload = "{}", OccurredOn = DateTimeOffset.UtcNow };
        await SeedMessagesAsync(dbName, msg);
        var (service, pubMock, _) = CreateService(dbName, o => o.BatchSize = 10);

        var longError = new string('X', 5000);
        pubMock
            .Setup(p => p.PublishAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(longError));

        // Act
        var result = await InvokeProcessBatchAsync(service);

        // Assert
        result.Should().Be(1);

        var factory = CreateFactory(dbName);
        await using var ctx = factory.CreateDbContext();
        var saved = await ctx.OutboxMessages.SingleAsync();
        saved.LastError.Should().NotBeNull();
        saved.LastError!.Length.Should().Be(2000);
        saved.LastError.Should().Be(longError[..2000]);
    }

    [Fact]
    public async Task ProcessBatchAsync_RespectsBatchSize()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString("N");
        var msgs = Enumerable.Range(1, 10)
            .Select(i => new OutboxMessage
            {
                EventType = $"E{i}",
                Payload = "{}",
                OccurredOn = DateTimeOffset.UtcNow.AddMinutes(-i),
            })
            .ToArray();
        await SeedMessagesAsync(dbName, msgs);
        var (service, pubMock, _) = CreateService(dbName, o => o.BatchSize = 4);

        pubMock
            .Setup(p => p.PublishAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await InvokeProcessBatchAsync(service);

        // Assert
        result.Should().Be(4);
        pubMock.Verify(
            p => p.PublishAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()),
            Times.Exactly(4));
    }

    // ================================================================
    // IOutboxLock Integration Tests (feature #82 Step 5/6)
    // ================================================================

    [Fact]
    public async Task ProcessBatchAsync_WhenLockSkips_AttemptCountNotIncremented()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString("N");
        var msg = new OutboxMessage { EventType = "Locked", Payload = "{}", OccurredOn = DateTimeOffset.UtcNow };
        await SeedMessagesAsync(dbName, msg);

        var lockMock = new Mock<IOutboxLock>();
        lockMock
            .Setup(l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false); // 模拟另一实例已持有锁
        var (service, pubMock, _) = CreateService(dbName, o => o.BatchSize = 10, lockMock: lockMock);

        // Act
        var result = await InvokeProcessBatchAsync(service);

        // Assert
        result.Should().Be(1, "被拉取的消息数仍是 1（拉取和加锁是两阶段）");
        pubMock.Verify(
            p => p.PublishAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "lock 失败时本实例不应投递");

        var factory = CreateFactory(dbName);
        await using var ctx = factory.CreateDbContext();
        var saved = await ctx.OutboxMessages.SingleAsync();
        saved.AttemptCount.Should().Be(0, "IOutboxLock 契约：lock 失败时 AttemptCount 不应自增 — 锁由谁释放后由谁接管重试");
        saved.SentTime.Should().BeNull();
        saved.LastError.Should().BeNull();
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenLockSucceeds_AttemptCountIncrementedNormally()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString("N");
        var msgs = new[]
        {
            new OutboxMessage { EventType = "Lockable1", Payload = "{}", OccurredOn = DateTimeOffset.UtcNow.AddMinutes(-2) },
            new OutboxMessage { EventType = "Lockable2", Payload = "{}", OccurredOn = DateTimeOffset.UtcNow.AddMinutes(-1) },
        };
        await SeedMessagesAsync(dbName, msgs);

        // 显式 lock mock：成功获锁 + 验证 ReleaseAsync 被调
        var lockMock = new Mock<IOutboxLock>();
        lockMock
            .Setup(l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var (service, pubMock, _) = CreateService(dbName, o => o.BatchSize = 10, lockMock: lockMock);
        pubMock
            .Setup(p => p.PublishAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await InvokeProcessBatchAsync(service);

        // Assert
        result.Should().Be(2);
        pubMock.Verify(
            p => p.PublishAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "lock 成功时所有消息必须被 publisher 投递");

        lockMock.Verify(
            l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "每条被处理的消息都必须尝试获锁");
        lockMock.Verify(
            l => l.ReleaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "lock 成功后必须显式 Release（成功路径）");

        var factory = CreateFactory(dbName);
        await using var ctx = factory.CreateDbContext();
        var saved = await ctx.OutboxMessages.OrderBy(m => m.OccurredOn).ToListAsync();
        saved.Should().AllSatisfy(m =>
        {
            m.AttemptCount.Should().Be(1, "lock 成功的消息必须按既有契约正常 ++AttemptCount");
            m.SentTime.Should().NotBeNull();
            m.LastError.Should().BeNull();
        });
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenPublisherThrows_LockReleasedAnyway()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString("N");
        var msg = new OutboxMessage { EventType = "Boom", Payload = "{}", OccurredOn = DateTimeOffset.UtcNow };
        await SeedMessagesAsync(dbName, msg);

        var lockMock = new Mock<IOutboxLock>();
        lockMock
            .Setup(l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var (service, pubMock, _) = CreateService(dbName, o => o.BatchSize = 10, lockMock: lockMock);

        // publisher 抛异常
        pubMock
            .Setup(p => p.PublishAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("publish failed"));

        // Act
        var result = await InvokeProcessBatchAsync(service);

        // Assert
        result.Should().Be(1);

        lockMock.Verify(
            l => l.ReleaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "publisher 抛异常的失败路径也必须 Release（try/finally 保证）");

        var factory = CreateFactory(dbName);
        await using var ctx = factory.CreateDbContext();
        var saved = await ctx.OutboxMessages.SingleAsync();
        saved.AttemptCount.Should().Be(1, "publisher 失败时按既有契约 ++AttemptCount");
        saved.SentTime.Should().BeNull();
        saved.LastError.Should().Be("publish failed");
    }

    // ================================================================
    // ExecuteAsync Tests
    // ================================================================

    [Fact]
    public async Task ExecuteAsync_ZeroProcessed_Delays()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString("N");
        var (service, pubMock, _) = CreateService(dbName, o =>
        {
            o.PollInterval = TimeSpan.FromMilliseconds(1);
            o.BatchSize = 10;
        });

        using var cts = new CancellationTokenSource();
        var executeTask = InvokeExecuteAsync(service, cts.Token);

        // Let a few poll cycles run (no messages → 0 processed each time)
        await Task.Delay(30);

        // Cancel and wait for clean exit
        cts.Cancel();
        await Task.WhenAny(executeTask, Task.Delay(3000));

        // Assert — no messages means PublishAsync never called
        pubMock.Verify(
            p => p.PublishAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_Cancellation_Stops()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString("N");
        var (service, _, _) = CreateService(dbName);
        using var cts = new CancellationTokenSource();

        // Act
        var executeTask = InvokeExecuteAsync(service, cts.Token);
        cts.Cancel();

        // Assert
        var completed = await Task.WhenAny(executeTask, Task.Delay(5000)) == executeTask;
        completed.Should().BeTrue("ExecuteAsync should complete within 5s after cancellation");
    }

    // ================================================================
    // Options Defaults
    // ================================================================

    [Fact]
    public void OutboxRelayOptions_DefaultValues()
    {
        var options = new OutboxRelayOptions();

        options.BatchSize.Should().Be(50);
        options.PollInterval.Should().Be(TimeSpan.FromSeconds(2));
        options.MaxAttempts.Should().Be(8);
    }
}
