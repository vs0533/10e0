using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TenE0.Core.Events.Outbox;

namespace TenE0.Core.Tests.Events.Outbox;

/// <summary>
/// BDD 验收测试 — Outbox Poison Message 死信查询 / 手动重试 / 导出。
///
/// 业务场景（#73）：
/// RelayService 把超过 MaxAttempts 的消息标记为 poison 后不再处理，
/// 这些消息会永久滞留 OutboxMessages 表。运维需要：
/// 1) 查询出所有 poison 消息用于排障
/// 2) 修复根因后手动重置 AttemptCount / LastError 让 Relay 下轮重新拾取
/// 3) 导出结构化字段用于离线分析
///
/// 验收口径：
/// - Poison 判定：SentTime IS NULL AND AttemptCount >= MaxAttempts
///   （与 RelayService ProcessBatchAsync 过滤未发送且未超阈的语义对偶）
/// - 阈值 MaxAttempts 必须复用 OutboxRelayOptions，不在 Admin 层重复定义
/// </summary>
[Trait("Category", "Acceptance")]
public sealed class OutboxAdminAcceptanceTests
{
    // ================================================================
    // Test Infrastructure (与 OutboxRelayServiceTests 对齐)
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

    private static IDbContextFactory<TestDbContext> CreateFactory(string dbName)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TestDbContextFactory(options);
    }

    private sealed class TestDbContextFactory(DbContextOptions<TestDbContext> options)
        : IDbContextFactory<TestDbContext>
    {
        public TestDbContext CreateDbContext() => new(options);
    }

    private static async Task SeedAsync(string dbName, params OutboxMessage[] messages)
    {
        var factory = CreateFactory(dbName);
        await using var ctx = factory.CreateDbContext();
        ctx.OutboxMessages.AddRange(messages);
        await ctx.SaveChangesAsync();
    }

    private static IOutboxAdmin CreateAdmin(
        string dbName,
        int maxAttempts)
    {
        var factory = CreateFactory(dbName);
        var sp = new ServiceCollection()
            .AddSingleton<IDbContextFactory<TestDbContext>>(factory)
            .BuildServiceProvider();
        var options = Options.Create(new OutboxRelayOptions { MaxAttempts = maxAttempts });
        return new OutboxAdminService<TestDbContext>(sp, options);
    }

    private static OutboxMessage NewMessage(
        string eventType,
        DateTimeOffset occurredOn,
        int attemptCount = 0,
        string? lastError = null,
        DateTimeOffset? sentTime = null)
        => new()
        {
            EventType = eventType,
            Payload = "{}",
            OccurredOn = occurredOn,
            AttemptCount = attemptCount,
            LastError = lastError,
            SentTime = sentTime,
        };

    // ================================================================
    // Scenario 1: 查询 — 仅返回 poison 消息
    // ================================================================

    [Fact]
    public async Task GivenMixedOutboxMessages_WhenQueryingPoison_ThenOnlyAttemptExhaustedUnsentsAreReturned()
    {
        // Arrange — MaxAttempts=3，poison 必须 AttemptCount>=3 且 SentTime==null
        var dbName = Guid.NewGuid().ToString("N");
        var poison = NewMessage("Poison", DateTimeOffset.UtcNow.AddMinutes(-5), attemptCount: 3, lastError: "boom");
        var poisonWayOver = NewMessage("WayOver", DateTimeOffset.UtcNow.AddMinutes(-4), attemptCount: 9, lastError: "boom2");
        var stillRetrying = NewMessage("Retrying", DateTimeOffset.UtcNow.AddMinutes(-3), attemptCount: 2, lastError: "transient");
        var alreadySent = NewMessage("Sent", DateTimeOffset.UtcNow.AddMinutes(-2), attemptCount: 5, lastError: "late", sentTime: DateTimeOffset.UtcNow.AddMinutes(-1));
        await SeedAsync(dbName, poison, poisonWayOver, stillRetrying, alreadySent);

        var admin = CreateAdmin(dbName, maxAttempts: 3);

        // Act
        var result = await admin.GetPoisonMessagesAsync(CancellationToken.None);

        // Then — 只有 poison + wayOver 被返回
        result.Should().HaveCount(2);
        result.Select(m => m.EventType).Should().BeEquivalentTo(new[] { "Poison", "WayOver" });
        result.Should().AllSatisfy(m =>
        {
            m.SentTime.Should().BeNull();
            m.AttemptCount.Should().BeGreaterThanOrEqualTo(3);
        });
    }

    // ================================================================
    // Scenario 2: 查询阈值复用 OutboxRelayOptions.MaxAttempts
    // ================================================================

    [Fact]
    public async Task GivenOutboxAdminConfiguredWithMaxAttempts5_WhenQueryingPoison_ThenThresholdIs5()
    {
        // Arrange — MaxAttempts=5 时，AttemptCount=4 视为正常重试中，5+ 才算 poison
        var dbName = Guid.NewGuid().ToString("N");
        var at4 = NewMessage("At4", DateTimeOffset.UtcNow, attemptCount: 4, lastError: "err");
        var at5 = NewMessage("At5", DateTimeOffset.UtcNow, attemptCount: 5, lastError: "err");
        var at7 = NewMessage("At7", DateTimeOffset.UtcNow, attemptCount: 7, lastError: "err");
        await SeedAsync(dbName, at4, at5, at7);

        var admin = CreateAdmin(dbName, maxAttempts: 5);

        // Act
        var result = await admin.GetPoisonMessagesAsync(CancellationToken.None);

        // Then — At4 被排除，At5/At7 进入 poison 列表
        result.Select(m => m.EventType).Should().BeEquivalentTo(new[] { "At5", "At7" });
    }

    // ================================================================
    // Scenario 3: 空表 → 返回空集
    // ================================================================

    [Fact]
    public async Task GivenEmptyOutboxTable_WhenQueryingPoison_ThenEmptyListReturned()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var admin = CreateAdmin(dbName, maxAttempts: 3);

        var result = await admin.GetPoisonMessagesAsync(CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ================================================================
    // Scenario 4: 重试 — 清零 AttemptCount 并清空 LastError
    // ================================================================

    [Fact]
    public async Task GivenPoisonMessage_WhenRetrying_ThenAttemptCountAndLastErrorAreResetAndSentTimeRemainsNull()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString("N");
        var poison = NewMessage("Poison", DateTimeOffset.UtcNow.AddMinutes(-10), attemptCount: 5, lastError: "handler crashed");
        await SeedAsync(dbName, poison);
        var admin = CreateAdmin(dbName, maxAttempts: 3);

        // Act
        var retried = await admin.RetryPoisonMessageAsync(Guid.Parse(poison.Id), CancellationToken.None);

        // Then
        retried.Should().BeTrue();

        var factory = CreateFactory(dbName);
        await using var ctx = factory.CreateDbContext();
        var saved = await ctx.OutboxMessages.SingleAsync();
        saved.AttemptCount.Should().Be(0, "重试必须清零 AttemptCount 让 Relay 重新拾取");
        saved.LastError.Should().BeNull("重试必须清空 LastError 避免下次失败时混淆原因");
        saved.SentTime.Should().BeNull("SentTime 保持 null，Relay 才能再次处理");
    }

    // ================================================================
    // Scenario 5: 重试后 Relay 重新拾取
    // ================================================================

    [Fact]
    public async Task GivenPoisonMessageRetriedManually_WhenRelayPollsAgain_ThenMessageIsProcessedAndSent()
    {
        // Arrange — 重置后下一轮 Relay 应能 Publish
        var dbName = Guid.NewGuid().ToString("N");
        var poison = NewMessage("Recovered", DateTimeOffset.UtcNow.AddMinutes(-10), attemptCount: 8, lastError: "old error");
        await SeedAsync(dbName, poison);
        var admin = CreateAdmin(dbName, maxAttempts: 3);
        await admin.RetryPoisonMessageAsync(Guid.Parse(poison.Id), CancellationToken.None);

        var publisherMock = new Mock<IOutboxPublisher>();
        publisherMock
            .Setup(p => p.PublishAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var serviceProviderMock = new Mock<IServiceProvider>();
        serviceProviderMock
            .Setup(sp => sp.GetService(typeof(IDbContextFactory<TestDbContext>)))
            .Returns(CreateFactory(dbName));
        serviceProviderMock
            .Setup(sp => sp.GetService(typeof(IOutboxPublisher)))
            .Returns(publisherMock.Object);
        // feature #82 集成：OutboxRelayService 现在依赖 IOutboxLock。Admin acceptance 场景里
        // 没有多实例竞争，用 NoOpOutboxLock 即可（始终 TryAcquire=true，让消息继续走到 publisher）。
        serviceProviderMock
            .Setup(sp => sp.GetService(typeof(IOutboxLock)))
            .Returns(new NoOpOutboxLock());

        var scopeMock = new Mock<IServiceScope>();
        scopeMock.SetupGet(s => s.ServiceProvider).Returns(serviceProviderMock.Object);
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

        var relay = new OutboxRelayService<TestDbContext>(
            scopeFactoryMock.Object,
            Options.Create(new OutboxRelayOptions { MaxAttempts = 3, BatchSize = 10 }),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OutboxRelayService<TestDbContext>>.Instance);

        // Act
        var batchResult = await InvokeProcessBatchAsync(relay);

        // Then — Relay 重新拾取 + 投递成功
        batchResult.Should().Be(1);
        publisherMock.Verify(
            p => p.PublishAsync(It.Is<OutboxMessage>(m => m.EventType == "Recovered"), It.IsAny<CancellationToken>()),
            Times.Once);

        var factory = CreateFactory(dbName);
        await using var ctx = factory.CreateDbContext();
        var saved = await ctx.OutboxMessages.SingleAsync();
        saved.SentTime.Should().NotBeNull("重置后 Relay 投递成功应写回 SentTime");
        saved.AttemptCount.Should().Be(1, "Relay 单轮再 +1");
    }

    // ================================================================
    // Scenario 6: 重试不存在的 ID → false
    // ================================================================

    [Fact]
    public async Task GivenNonExistentMessageId_WhenRetrying_ThenReturnsFalseAndNoSideEffects()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var existing = NewMessage("Alive", DateTimeOffset.UtcNow, attemptCount: 1);
        await SeedAsync(dbName, existing);
        var admin = CreateAdmin(dbName, maxAttempts: 3);

        var retried = await admin.RetryPoisonMessageAsync(Guid.NewGuid(), CancellationToken.None);

        retried.Should().BeFalse("不存在的 ID 必须返回 false 而非抛异常，运维脚本可幂等轮询");

        var factory = CreateFactory(dbName);
        await using var ctx = factory.CreateDbContext();
        var all = await ctx.OutboxMessages.ToListAsync();
        all.Should().ContainSingle(m => m.EventType == "Alive");
    }

    // ================================================================
    // Scenario 7: 导出 — 结构化 DTO 包含排障关键字段
    // ================================================================

    [Fact]
    public async Task GivenPoisonMessages_WhenExporting_ThenStructuredDtosContainEventTypePayloadLastErrorAttemptCount()
    {
        // Arrange — 多条 poison 验证结构化导出
        var dbName = Guid.NewGuid().ToString("N");
        var a = NewMessage("EventA", DateTimeOffset.UtcNow.AddMinutes(-3), attemptCount: 4, lastError: "timeout");
        var b = NewMessage("EventB", DateTimeOffset.UtcNow.AddMinutes(-2), attemptCount: 5, lastError: "deserialization");
        await SeedAsync(dbName, a, b);
        var admin = CreateAdmin(dbName, maxAttempts: 3);

        // Act
        var exported = await admin.ExportPoisonMessagesAsync(CancellationToken.None);

        // Then — 字段全 + 值正确
        exported.Should().HaveCount(2);
        exported.Should().AllSatisfy(dto =>
        {
            dto.EventType.Should().NotBeNullOrEmpty();
            dto.Payload.Should().NotBeNullOrEmpty();
            dto.LastError.Should().NotBeNullOrEmpty();
            dto.AttemptCount.Should().BeGreaterThanOrEqualTo(3);
            dto.Id.Should().NotBe(Guid.Empty);
            dto.OccurredOn.Should().NotBe(default);
        });

        var dtoA = exported.Single(d => d.EventType == "EventA");
        dtoA.LastError.Should().Be("timeout");
        dtoA.AttemptCount.Should().Be(4);
        dtoA.Payload.Should().Be("{}");
    }

    // ================================================================
    // Scenario 8: 重试空字符串 LastError 视为已清空
    // ================================================================

    [Fact]
    public async Task GivenPoisonMessageWithEmptyLastError_WhenRetrying_ThenLastErrorIsNull()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var msg = NewMessage("PoisonEmpty", DateTimeOffset.UtcNow, attemptCount: 5, lastError: "");
        await SeedAsync(dbName, msg);
        var admin = CreateAdmin(dbName, maxAttempts: 3);

        await admin.RetryPoisonMessageAsync(Guid.Parse(msg.Id), CancellationToken.None);

        var factory = CreateFactory(dbName);
        await using var ctx = factory.CreateDbContext();
        var saved = await ctx.OutboxMessages.SingleAsync();
        saved.LastError.Should().BeNull();
        saved.AttemptCount.Should().Be(0);
    }

    // ================================================================
    // Helper: reflect into RelayService.ProcessBatchAsync
    // ================================================================

    private static Task<int> InvokeProcessBatchAsync(OutboxRelayService<TestDbContext> service)
    {
        var method = typeof(OutboxRelayService<TestDbContext>)
            .GetMethod("ProcessBatchAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                     ?? throw new InvalidOperationException("ProcessBatchAsync not found");
        return (Task<int>)method.Invoke(service, [CancellationToken.None])!;
    }
}
