using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using TenE0.Core.Events.Outbox;

namespace TenE0.Core.Tests.Events.Outbox;

/// <summary>
/// SqlServerOutboxLock 单元测试 — Happy Path（issue #81 / #74 子任务）
///
/// <para>
/// <b>测试范围</b>：本文件只验证 SqlServerOutboxLock 在 LINQ 路径上的契约行为
/// （assertion：LockedUntil / LockedByInstance 写入、Release 清空、所有权校验、
/// 锁过期可抢占、缺失消息返回 false）。
/// </para>
///
/// <para>
/// <b>为什么用 InMemory 而非真实 SqlServer</b>：
/// 真实 SqlServer provider（<c>Microsoft.EntityFrameworkCore.SqlServer</c>）不在测试项目
/// 依赖中，且 SqlServer 行级锁（<c>UPDLOCK, READPAST</c>）需要真实数据库实例才能验证；
/// issue 明确"单元测试用 InMemory provider 跑 happy path；并发安全测试放到 (3)"。
/// InMemory 下 <c>UPDLOCK</c> / <c>SKIP LOCKED</c> 是 noop，<c>UPDATE</c> 实际执行，
/// 因此断言"Lock 字段被写入 / 清空"即等同验证 LINQ 路径的行为契约。
/// </para>
///
/// <para>
/// <b>不验证</b>：
/// - SqlServer <c>UPDATE ... SET LockedByInstance = ...</c> 的真实执行（SQL 路径走
///   <c>ExecuteSqlInterpolatedAsync</c>，InMemory 上不支持该 API）
/// - 跨进程 / 多实例并发抢占（属于 issue (3) 范畴，需 SqlServer Testcontainers）
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class SqlServerOutboxLockTests
{
    // ================================================================
    // Test Infrastructure — 复用 #80 acceptance 的 TestDbContext 模板
    //   自有副本而非 using static 引用，避免 OutboxLockProviderAcceptanceTests
    //   改名/重构时本文件受影响
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

    private static OutboxMessage NewMessage(
        string eventType,
        DateTimeOffset occurredOn,
        string? lockedByInstance = null,
        DateTimeOffset? lockedUntil = null)
        => new()
        {
            EventType = eventType,
            Payload = "{}",
            OccurredOn = occurredOn,
            LockedByInstance = lockedByInstance,
            LockedUntil = lockedUntil,
        };

    private static async Task<string> SeedAsync(
        IDbContextFactory<TestDbContext> factory,
        params OutboxMessage[] messages)
    {
        await using var ctx = factory.CreateDbContext();
        ctx.OutboxMessages.AddRange(messages);
        await ctx.SaveChangesAsync();
        return messages[0].Id;
    }

    private static async Task<OutboxMessage?> ReloadAsync(
        IDbContextFactory<TestDbContext> factory,
        string messageId)
    {
        await using var ctx = factory.CreateDbContext();
        return await ctx.OutboxMessages.FindAsync(messageId);
    }

    // ================================================================
    // Scenario: Happy Path — TryAcquire 写入 LockedUntil / LockedByInstance
    // ================================================================

    [Fact]
    public async Task GivenSqlServerLockAndUnlockedMessage_WhenTryAcquire_ThenLockFieldsAreWritten()
    {
        // Arrange — 新建一条未被任何实例持有的消息
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var msgId = await SeedAsync(factory, NewMessage("OrderPlaced", DateTimeOffset.UtcNow));

        IOutboxLock sut = new SqlServerOutboxLock<TestDbContext>(factory);

        // Act
        var beforeCall = DateTimeOffset.UtcNow;
        var acquired = await sut.TryAcquireAsync(
            messageId: msgId,
            instanceId: "instance-A",
            lease: TimeSpan.FromMinutes(1),
            cancellationToken: CancellationToken.None);

        // Then — 拿到锁 + 行状态被写入
        acquired.Should().BeTrue(
            "未被任何实例持有的消息，本实例 TryAcquire 必须成功（Issue #81 happy path 验收口径）");
        var reloaded = await ReloadAsync(factory, msgId);
        reloaded.Should().NotBeNull("Seed 后的行必须仍在表中");
        reloaded!.LockedByInstance.Should().Be(
            "instance-A",
            "TryAcquire 必须把持有者实例 ID 写入 LockedByInstance — 所有权判断的核心依据");
        reloaded.LockedUntil.Should().NotBeNull(
            "TryAcquire 必须写入 LockedUntil — 租约到期的判定依据");
        reloaded.LockedUntil!.Value.Should().BeAfter(
            beforeCall,
            "LockedUntil 必须指向调用时刻之后（即 now + lease），标识租约期内有效");
        reloaded.LockedUntil!.Value.Should().BeBefore(
            beforeCall.AddMinutes(2),
            "LockedUntil 不应远超 lease 上限（防止实现误把 lease 当成 ms 或秒）");
    }

    [Fact]
    public async Task GivenSqlServerLockAndSameInstanceReacquire_WhenTryAcquire_ThenSucceedsAsReentrant()
    {
        // Arrange — 同一实例再次 TryAcquire 已持有的行：契约未禁止"自持自取"，
        // 但必须保持 LockedByInstance 不变（即不把锁"转交"给错乱值）
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var msgId = await SeedAsync(factory, NewMessage("E", DateTimeOffset.UtcNow));

        IOutboxLock sut = new SqlServerOutboxLock<TestDbContext>(factory);
        await sut.TryAcquireAsync(msgId, "instance-A", TimeSpan.FromMinutes(1), CancellationToken.None);

        // Act — 同一实例再调一次
        var acquired = await sut.TryAcquireAsync(
            messageId: msgId,
            instanceId: "instance-A",
            lease: TimeSpan.FromMinutes(1),
            cancellationToken: CancellationToken.None);

        // Then — 拿得到（条件 LockedByInstance == A && LockedUntil > now 视为"被自己持有"，仍可覆盖写）
        acquired.Should().BeTrue(
            "LINQ 路径不区分'自己持有'与'他人持有'，只要租约未到期就允许覆盖 — 与 SQL 路径 UPDATE WHERE 等价");
        var reloaded = await ReloadAsync(factory, msgId);
        reloaded!.LockedByInstance.Should().Be(
            "instance-A",
            "自持自取后 LockedByInstance 仍应等于调用方实例 ID — 不应被错乱覆盖");
    }

    // ================================================================
    // Scenario: Conflict — 其他实例持锁时本实例必须跳过
    // ================================================================

    [Fact]
    public async Task GivenSqlServerLockAndMessageHeldByOtherInstance_WhenTryAcquire_ThenReturnsFalse()
    {
        // Arrange — instance-B 已经持有
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var msgId = await SeedAsync(factory, NewMessage("E", DateTimeOffset.UtcNow));

        IOutboxLock sut = new SqlServerOutboxLock<TestDbContext>(factory);
        await sut.TryAcquireAsync(msgId, "instance-B", TimeSpan.FromMinutes(1), CancellationToken.None);

        // Act — instance-A 抢锁
        var acquired = await sut.TryAcquireAsync(
            messageId: msgId,
            instanceId: "instance-A",
            lease: TimeSpan.FromMinutes(1),
            cancellationToken: CancellationToken.None);

        // Then — 必须跳过，且原持有者标识不被覆盖
        acquired.Should().BeFalse(
            "其他实例仍持有锁时，本实例 TryAcquire 必须返回 false — 这是 #74 已知风险 #1 的核心防护");
        var reloaded = await ReloadAsync(factory, msgId);
        reloaded!.LockedByInstance.Should().Be(
            "instance-B",
            "其他人持锁时本实例不能覆盖 LockedByInstance，否则会破坏所有权语义");
    }

    // ================================================================
    // Scenario: Expiry — 租约到期后任何实例可重新拾取
    // ================================================================

    [Fact]
    public async Task GivenSqlServerLockAndExpiredLease_WhenTryAcquire_ThenSucceedsAndOverwritesHolder()
    {
        // Arrange — LockedUntil 已在过去，视为锁已过期
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var msgId = await SeedAsync(factory, NewMessage(
            "E",
            DateTimeOffset.UtcNow,
            lockedByInstance: "instance-old",
            lockedUntil: DateTimeOffset.UtcNow.AddSeconds(-1)));

        IOutboxLock sut = new SqlServerOutboxLock<TestDbContext>(factory);

        // Act
        var acquired = await sut.TryAcquireAsync(
            messageId: msgId,
            instanceId: "instance-A",
            lease: TimeSpan.FromMinutes(1),
            cancellationToken: CancellationToken.None);

        // Then — 锁过期 = 任何实例可重新拾取
        acquired.Should().BeTrue(
            "租约到期后 (LockedUntil <= now) 锁自动失效，任何实例 TryAcquire 必须能拿到锁（Issue #81 happy path）");
        var reloaded = await ReloadAsync(factory, msgId);
        reloaded!.LockedByInstance.Should().Be(
            "instance-A",
            "新实例必须覆盖旧的持有者标识，便于排障（旧的 instance-old 已失联）");
        reloaded.LockedUntil.Should().NotBeNull("新持有者必须写入新的 LockedUntil");
    }

    // ================================================================
    // Scenario: Release — 本实例持有时 Release 清空锁字段
    // ================================================================

    [Fact]
    public async Task GivenSqlServerLockAndOwnedMessage_WhenRelease_ThenLockFieldsAreCleared()
    {
        // Arrange — 本实例持有，正常 Release
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var msgId = await SeedAsync(factory, NewMessage("E", DateTimeOffset.UtcNow));

        IOutboxLock sut = new SqlServerOutboxLock<TestDbContext>(factory);
        await sut.TryAcquireAsync(msgId, "instance-A", TimeSpan.FromMinutes(1), CancellationToken.None);

        // Act
        await sut.ReleaseAsync(msgId, "instance-A", CancellationToken.None);

        // Then — Release 必须清空 LockedUntil / LockedByInstance，让下轮 Relay 可拾取
        var reloaded = await ReloadAsync(factory, msgId);
        reloaded!.LockedUntil.Should().BeNull(
            "Release 必须清空 LockedUntil — 让其他实例下轮可拾取该行");
        reloaded.LockedByInstance.Should().BeNull(
            "Release 必须清空 LockedByInstance — 避免误把行标记为'仍被处理中'");
    }

    // ================================================================
    // Scenario: Ownership — 其他人持有时 Release 不能误释放
    // ================================================================

    [Fact]
    public async Task GivenSqlServerLockAndMessageHeldByOtherInstance_WhenRelease_ThenDoesNotClear()
    {
        // Arrange — instance-B 持有
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var msgId = await SeedAsync(factory, NewMessage("E", DateTimeOffset.UtcNow));

        IOutboxLock sut = new SqlServerOutboxLock<TestDbContext>(factory);
        await sut.TryAcquireAsync(msgId, "instance-B", TimeSpan.FromMinutes(1), CancellationToken.None);

        // Act — instance-A 误调 Release
        Func<Task> act = () => sut.ReleaseAsync(msgId, "instance-A", CancellationToken.None);

        // Then — 不抛异常（契约幂等），且不能动 B 的锁
        await act.Should().NotThrowAsync(
            "Release 契约要求幂等；其他实例持有时调用 Release 必须不抛异常");
        var reloaded = await ReloadAsync(factory, msgId);
        reloaded!.LockedByInstance.Should().Be(
            "instance-B",
            "所有权校验：仅当 LockedByInstance == 调用方 instanceId 时才清空；不能误释放他人的锁");
        reloaded.LockedUntil.Should().NotBeNull(
            "他人持有时 Release 必须保留 LockedUntil — 否则会破坏其租约语义");
    }

    // ================================================================
    // Scenario: Missing Row — messageId 不存在时返回 false，不抛异常
    // ================================================================

    [Fact]
    public async Task GivenSqlServerLockAndMissingMessage_WhenTryAcquire_ThenReturnsFalse()
    {
        // Arrange — messageId 不存在
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);

        IOutboxLock sut = new SqlServerOutboxLock<TestDbContext>(factory);

        // Act
        var acquired = await sut.TryAcquireAsync(
            messageId: "non-existent-id",
            instanceId: "instance-A",
            lease: TimeSpan.FromMinutes(1),
            cancellationToken: CancellationToken.None);

        // Then — 行不存在时返回 false，不抛异常
        acquired.Should().BeFalse(
            "行不存在时 TryAcquire 必须返回 false — 不能因 'no row affected' 抛异常破坏 Relay");
    }

    // ================================================================
    // Scenario: TimeProvider 注入（issue #96）— FakeTime 推进后 lease 视为过期
    // ================================================================

    [Fact]
    public async Task GivenSqlServerLockAndActiveLease_WhenFakeTimeAdvancesPastLease_ThenReacquireSucceeds()
    {
        // Arrange — instance-A 在 t=0 抢到 lease=10min 的锁
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var start = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(start);
        var msgId = await SeedAsync(factory, NewMessage("TimeProbe", start));

        IOutboxLock sut = new SqlServerOutboxLock<TestDbContext>(factory, timeProvider);
        var firstAcquire = await sut.TryAcquireAsync(
            messageId: msgId, instanceId: "instance-A",
            lease: TimeSpan.FromMinutes(10), cancellationToken: CancellationToken.None);
        firstAcquire.Should().BeTrue();

        // Act — 推进 15 分钟（超过 lease），instance-B 试图重新抢占
        timeProvider.Advance(TimeSpan.FromMinutes(15));
        var reacquireByB = await sut.TryAcquireAsync(
            messageId: msgId, instanceId: "instance-B",
            lease: TimeSpan.FromMinutes(10), cancellationToken: CancellationToken.None);

        // Assert — 锁过期判定走 TimeProvider（issue #96 修复），B 抢占成功
        reacquireByB.Should().BeTrue(
            "FakeTime 推进 15min 超过 lease=10min，Lock 视为过期，新实例必须能抢占（issue #96 修复 TimeProvider 注入后验收测试不再 Thread.Sleep）");
        var reloaded = await ReloadAsync(factory, msgId);
        reloaded!.LockedByInstance.Should().Be("instance-B");
        reloaded.LockedUntil.Should().Be(start.AddMinutes(15).AddMinutes(10),
            "LockedUntil = 推进后的 now + lease = 12:15 + 10min = 12:25");
    }

    [Fact]
    public async Task GivenSqlServerLockAndActiveLease_WhenFakeTimeNotPastLease_ThenReacquireFails()
    {
        // Arrange — lease=10min，t=0 抢锁
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var start = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(start);
        var msgId = await SeedAsync(factory, NewMessage("TimeProbe2", start));

        IOutboxLock sut = new SqlServerOutboxLock<TestDbContext>(factory, timeProvider);
        await sut.TryAcquireAsync(msgId, "instance-A", TimeSpan.FromMinutes(10), CancellationToken.None);

        // Act — 只推进 5min（lease 还没到），B 试图抢占
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        var reacquireByB = await sut.TryAcquireAsync(
            messageId: msgId, instanceId: "instance-B",
            lease: TimeSpan.FromMinutes(10), cancellationToken: CancellationToken.None);

        // Assert — 锁未过期，B 抢占失败
        reacquireByB.Should().BeFalse(
            "lease=10min 只推进 5min，Lock 仍有效，其他实例不能抢占（issue #96 验证 TimeProvider 真实影响过期判定）");
        var reloaded = await ReloadAsync(factory, msgId);
        reloaded!.LockedByInstance.Should().Be("instance-A");
    }

    // ================================================================
    // Scenario: Idempotent Release — 不存在的 messageId Release 不抛异常
    // ================================================================

    [Fact]
    public async Task GivenSqlServerLockAndMissingMessage_WhenRelease_ThenDoesNotThrow()
    {
        // Arrange — 释放一个根本不存在的 messageId
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);

        IOutboxLock sut = new SqlServerOutboxLock<TestDbContext>(factory);

        // Act + Then — Release 契约幂等：行不存在时不能抛异常
        Func<Task> act = () => sut.ReleaseAsync(
            messageId: "non-existent-id",
            instanceId: "instance-A",
            cancellationToken: CancellationToken.None);
        await act.Should().NotThrowAsync(
            "Release 在行不存在时必须 no-op — Relay 重启后再次 Release 已处理消息是正常路径");
    }

    // ================================================================
    // Scenario: Null Guard — messageId 为空时返回 false，不抛异常
    // ================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GivenSqlServerLockAndInvalidMessageId_WhenTryAcquire_ThenReturnsFalse(string? invalidId)
    {
        // Arrange — messageId 校验失败
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var msgId = await SeedAsync(factory, NewMessage("E", DateTimeOffset.UtcNow));

        IOutboxLock sut = new SqlServerOutboxLock<TestDbContext>(factory);

        // Act
        var acquired = await sut.TryAcquireAsync(
            messageId: invalidId!,
            instanceId: "instance-A",
            lease: TimeSpan.FromMinutes(1),
            cancellationToken: CancellationToken.None);

        // Then — 防御性短路：空 messageId 直接返回 false
        acquired.Should().BeFalse(
            "messageId 为 null/空串/空白时 TryAcquire 必须短路返回 false，避免无意义的 DbContext 操作");
    }
}
