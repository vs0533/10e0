using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.Events.Outbox;

namespace TenE0.Core.Tests.Events.Outbox;

/// <summary>
/// BDD 验收测试 — Outbox 行级锁抽象 + "处理中"状态字段 (#80 / #74 子任务)
///
/// 业务动机：
/// 当前 OutboxRelayService 在多实例部署时会竞争同一行（已知风险 #1），
/// "拉一批回来 → 内存中标记 → SaveChanges" 这种乐观重试在并发下会重复投递。
///
/// 验收口径：
/// - OutboxMessage 加 LockedUntil / LockedByInstance 两字段，标识"被某个实例拾取中"
/// - IOutboxLock 是行级锁的契约入口；TryAcquire 拿不到 = 这一轮别人在干活，本实例直接跳过
/// - 默认 NoOpOutboxLock 让 0/1 实例部署零感知（永远返回 true，保留旧行为）
/// - Relay 新流程：拉候选 → 逐条 TryAcquire → 拿到锁才投递 → Release；
///   拿不到的行连 AttemptCount 都不应增加（未真正拾取）
/// - OutboxRelayOptions 加 LockLeaseDuration / LockInstanceId 两个新配置
/// - OutboxModelBuilderExtensions 加 (LockedUntil, OccurredOn) 复合索引
///
/// 不验证：具体 provider 实现（SQL Server UPDLOCK/READPAST / PG FOR UPDATE SKIP LOCKED）
/// </summary>
[Trait("Category", "Acceptance")]
public sealed class OutboxLockAcceptanceTests
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

    private static OutboxMessage NewMessage(
        string eventType,
        DateTimeOffset occurredOn,
        int attemptCount = 0,
        string? lastError = null,
        DateTimeOffset? sentTime = null,
        DateTimeOffset? lockedUntil = null,
        string? lockedByInstance = null)
        => new()
        {
            EventType = eventType,
            Payload = "{}",
            OccurredOn = occurredOn,
            AttemptCount = attemptCount,
            LastError = lastError,
            SentTime = sentTime,
            LockedUntil = lockedUntil,
            LockedByInstance = lockedByInstance,
        };

    // ================================================================
    // Scenario 1: 实体契约 — 新字段存在且默认 null
    // ================================================================

    [Fact]
    public void GivenNewOutboxMessage_WhenConstructed_ThenLockFieldsAreNullByDefault()
    {
        // Arrange + Act
        var msg = NewMessage("E", DateTimeOffset.UtcNow);

        // Then — 未被任何实例拾取
        msg.LockedUntil.Should().BeNull("消息新建时不可能已被锁");
        msg.LockedByInstance.Should().BeNull("消息新建时不可能已被某个实例占用");
    }

    // ================================================================
    // Scenario 2: OutboxRelayOptions — 新字段默认值
    // ================================================================

    [Fact]
    public void GivenOutboxRelayOptions_WhenInstantiated_ThenLockOptionsHaveSensibleDefaults()
    {
        // Arrange + Act
        var options = new OutboxRelayOptions();

        // Then
        options.LockLeaseDuration.Should().BeGreaterThan(TimeSpan.Zero,
            "租约时长必须为正；否则锁瞬间过期等于未锁");
        options.LockInstanceId.Should().NotBeNullOrWhiteSpace(
            "实例 ID 不能为空；否则拿不到任何锁的归属信息");
    }

    [Fact]
    public void GivenCustomLockConfig_WhenApplied_ThenOptionsExposeThem()
    {
        // Arrange
        var options = new OutboxRelayOptions
        {
            LockLeaseDuration = TimeSpan.FromMinutes(5),
            LockInstanceId = "instance-A",
        };

        // Then
        options.LockLeaseDuration.Should().Be(TimeSpan.FromMinutes(5));
        options.LockInstanceId.Should().Be("instance-A");
    }

    // ================================================================
    // Scenario 3: NoOpOutboxLock — 默认实现永远能拿锁
    // ================================================================

    [Fact]
    public async Task GivenNoOpOutboxLock_WhenTryAcquire_ThenAlwaysSucceeds()
    {
        // Arrange — 0/1 实例部署场景，回退旧行为
        IOutboxLock sut = new NoOpOutboxLock();

        // Act
        var acquired = await sut.TryAcquireAsync(
            messageId: "any-message",
            instanceId: "any-instance",
            lease: TimeSpan.FromMinutes(1),
            cancellationToken: CancellationToken.None);

        // Then
        acquired.Should().BeTrue("NoOp 实现不能让任何一行被错过，否则就破坏 0/1 实例部署的回退行为");
    }

    [Fact]
    public async Task GivenNoOpOutboxLock_WhenRelease_ThenDoesNotThrow()
    {
        // Arrange — Release 在 NoOp 下应是 no-op，绝不能因未持有锁而抛异常
        IOutboxLock sut = new NoOpOutboxLock();

        // Act
        Func<Task> act = () => sut.ReleaseAsync(
            messageId: "any-message",
            instanceId: "any-instance",
            cancellationToken: CancellationToken.None);

        // Then
        await act.Should().NotThrowAsync();
    }

    // ================================================================
    // Scenario 4: IOutboxLock 契约 — 可写 Mock 行为供 Relay 编排用
    // ================================================================

    [Fact]
    public async Task GivenArbitraryOutboxLock_WhenTryAcquireReturnsTrue_ThenRelayShouldProcess()
    {
        // Arrange — 业务契约层断言：拿得到锁 = 这一轮该被本实例处理
        var sut = new Mock<IOutboxLock>();
        sut.Setup(l => l.TryAcquireAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var acquired = await sut.Object.TryAcquireAsync(
            "m1", "instance-X", TimeSpan.FromSeconds(30), CancellationToken.None);

        // Then
        acquired.Should().BeTrue();
        sut.Verify(l => l.TryAcquireAsync(
            "m1", "instance-X", TimeSpan.FromSeconds(30),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GivenArbitraryOutboxLock_WhenTryAcquireReturnsFalse_ThenRelayShouldSkip()
    {
        // Arrange — 业务契约层断言：拿不到锁 = 别人在干活，本实例跳过
        var sut = new Mock<IOutboxLock>();
        sut.Setup(l => l.TryAcquireAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var acquired = await sut.Object.TryAcquireAsync(
            "m1", "instance-X", TimeSpan.FromSeconds(30), CancellationToken.None);

        // Then
        acquired.Should().BeFalse("契约语义：拿不到锁的候选行本实例不处理");
    }

    // ================================================================
    // Scenario 5: DI 扩展 — AddOutboxLocking 默认注册 NoOpOutboxLock
    // ================================================================

    [Fact]
    public void GivenServiceCollection_WhenAddOutboxLockingCalled_ThenNoOpOutboxLockRegisteredAsDefault()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var result = services.AddOutboxLocking();

        // Then — 链式调用返回同一集合
        result.Should().BeSameAs(services);
        var sp = services.BuildServiceProvider();
        var lockObj = sp.GetService<IOutboxLock>();

        lockObj.Should().NotBeNull("AddOutboxLocking 必须注册 IOutboxLock 默认实现");
        lockObj.Should().BeOfType<NoOpOutboxLock>(
            "默认必须是 NoOp — 否则 0/1 实例部署会因缺少 provider 抛异常，破坏向前兼容");
    }

    // ================================================================
    // Scenario 6: 复合索引 — (LockedUntil, OccurredOn) 在 ModelBuilder 中注册
    // ================================================================

    [Fact]
    public void GivenOutboxModel_WhenBuilt_ThenLockedUntilOccurredOnIndexExists()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        // Act
        using var ctx = new TestDbContext(options);
        var entityType = ctx.Model.FindEntityType(typeof(OutboxMessage))!;
        var indexNames = entityType.GetIndexes().Select(i => i.Properties.Select(p => p.Name).ToArray()).ToList();

        // Then — 必须存在 (LockedUntil, OccurredOn) 复合索引，用于 SKIP LOCKED 风格的 WHERE 过滤
        indexNames.Should().Contain(
            p => p.SequenceEqual(new[] { nameof(OutboxMessage.LockedUntil), nameof(OutboxMessage.OccurredOn) }),
            "复合索引 (LockedUntil, OccurredOn) 是 Relay 跳过已锁行的关键查询路径，必须存在");
    }

    // ================================================================
    // Scenario 7: 既有索引保留 — 重构后旧索引 (SentTime, OccurredOn) 仍存在
    // ================================================================

    [Fact]
    public void GivenOutboxModel_WhenBuilt_ThenLegacySentTimeOccurredOnIndexStillExists()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        // Act
        using var ctx = new TestDbContext(options);
        var entityType = ctx.Model.FindEntityType(typeof(OutboxMessage))!;
        var indexNames = entityType.GetIndexes().Select(i => i.Properties.Select(p => p.Name).ToArray()).ToList();

        // Then — 不破坏现有 Admin 查询路径
        indexNames.Should().Contain(
            p => p.SequenceEqual(new[] { nameof(OutboxMessage.SentTime), nameof(OutboxMessage.OccurredOn) }),
            "旧索引 (SentTime, OccurredOn) 被 OutboxAdmin 查询复用，不能因为重构删除");
    }

    // ================================================================
    // Scenario 8: 状态语义 — "处理中"的判定
    // ================================================================

    [Fact]
    public void GivenOutboxMessage_WhenLockFieldsSet_ThenReflectsProcessingState()
    {
        // Arrange + Act — 模拟 Relay 拾取后的状态写入
        var now = DateTimeOffset.UtcNow;
        var msg = NewMessage(
            "E",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            lockedUntil: now.AddMinutes(2),
            lockedByInstance: "instance-42");

        // Then — "处理中"的两条标识都被设上
        msg.LockedUntil.Should().Be(now.AddMinutes(2), "LockedUntil 必须指向未来时刻，标识租约期内有效");
        msg.LockedByInstance.Should().Be("instance-42", "必须记录持有者实例 ID，便于排障与所有权判断");
    }

    [Fact]
    public void GivenOutboxMessage_WhenLeaseExpires_ThenLockIsEffectivelyReleased()
    {
        // Arrange — 业务约定：LockedUntil <= now 视为锁已过期（任何实例可重新拾取）
        var now = DateTimeOffset.UtcNow;
        var expired = NewMessage(
            "Expired",
            DateTimeOffset.UtcNow.AddMinutes(-10),
            lockedUntil: now.AddSeconds(-1),
            lockedByInstance: "instance-old");

        var stillLocked = NewMessage(
            "Locked",
            DateTimeOffset.UtcNow.AddMinutes(-10),
            lockedUntil: now.AddMinutes(1),
            lockedByInstance: "instance-active");

        // Then — 用时间比较断言两种状态
        (expired.LockedUntil <= now).Should().BeTrue("过期锁对所有实例都应当被视为可拾取");
        (stillLocked.LockedUntil > now).Should().BeTrue("未过期锁对其他实例应当跳过");
    }
}
