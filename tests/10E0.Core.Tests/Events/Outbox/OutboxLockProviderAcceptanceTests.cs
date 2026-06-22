using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TenE0.Core.DependencyInjection;
using TenE0.Core.Events.Outbox;

namespace TenE0.Core.Tests.Events.Outbox;

/// <summary>
/// BDD 验收测试 — Outbox 行级锁的 SQL Server / PostgreSQL provider 实现 (#81 / #74 子任务)
///
/// 业务动机：
/// #80 已落地 IOutboxLock 抽象 + NoOpOutboxLock 默认实现；多实例部署下仍需要真正
/// 行级锁的 provider 来避免 Relay 重复投递。本任务交付两个具体实现：
/// - SqlServerOutboxLock：UPDLOCK, READPAST 提示拼到 SELECT TOP (N) ...
/// - PostgresOutboxLock：FOR UPDATE SKIP LOCKED 提示
///
/// 验收口径：
/// - OutboxRelayOptions.LockProvider 新增枚举：&quot;RowLock&quot; | &quot;Distributed&quot; | &quot;None&quot;，
///   默认 &quot;None&quot;（与 NoOpOutboxLock 等价，0/1 实例部署零感知）
/// - OutboxLockProvider 抽象：根据 ProviderName 字符串命名匹配选择实现
///   （&quot;Microsoft.EntityFrameworkCore.SqlServer&quot; / &quot;Npgsql.EntityFrameworkCore.PostgreSQL&quot;），
///   不绑具体 provider 包类型，避免 TenE0.Core 强依赖 SqlServer/PG NuGet
/// - TryAcquire 成功时该行 LockedUntil / LockedByInstance 必须被写入（happy path）
/// - Release 必须校验所有权：仅当 LockedByInstance == 当前实例才清空；其他人持有时不抛异常
/// - 锁租约到期 (LockedUntil &lt;= now) 即视为锁失效
/// - DI 默认仍注册 NoOpOutboxLock；显式 AddOutboxRowLock 选择器后才暴露 provider 工厂
///
/// 不验证：
/// - SQL 提示真的生效（需要真实 SqlServer/PG 实例，属于 issue (3) 并发安全测试范畴）
/// - 跨进程 / 多实例并发抢占（属于 issue (3)）
/// </summary>
[Trait("Category", "Acceptance")]
public sealed class OutboxLockProviderAcceptanceTests
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

    private static IDbContextFactory<TestDbContext> CreateInMemoryFactory(string dbName)
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
    // Scenario 1: OutboxRelayOptions — LockProvider 新字段默认值 + 合法值
    // ================================================================

    [Fact]
    public void GivenOutboxRelayOptions_WhenInstantiated_ThenLockProviderDefaultsToNone()
    {
        // Arrange + Act
        var options = new OutboxRelayOptions();

        // Then — 默认 None 让 0/1 实例部署零感知（与 #80 NoOpOutboxLock 等价）
        options.LockProvider.Should().Be(
            OutboxLockProviderKind.None,
            "默认必须是 None — 与 #80 NoOpOutboxLock 等价；0/1 实例部署不感知多实例锁逻辑");
    }

    [Fact]
    public void GivenOutboxRelayOptions_WhenLockProviderSetToRowLock_ThenValueIsExposed()
    {
        // Arrange
        var options = new OutboxRelayOptions { LockProvider = OutboxLockProviderKind.RowLock };

        // Then
        options.LockProvider.Should().Be(
            OutboxLockProviderKind.RowLock,
            "LockProvider 必须可显式配置为 RowLock 以启用 SQL Server/PG 行级锁 provider");
    }

    [Fact]
    public void GivenOutboxRelayOptions_WhenLockProviderSetToDistributed_ThenValueIsExposed()
    {
        // Arrange
        var options = new OutboxRelayOptions { LockProvider = OutboxLockProviderKind.Distributed };

        // Then — Distributed 留给后续 Redis/SqlServer sp_getapplock 等场景，本任务不实现
        options.LockProvider.Should().Be(
            OutboxLockProviderKind.Distributed,
            "LockProvider 必须支持 Distributed 枚举值以兼容未来的 Redis 等实现");
    }

    // ================================================================
    // Scenario 2: OutboxLockProvider — 按 ProviderName 字符串命名匹配选择 provider
    //   命名匹配只读 ProviderName 字符串（不绑具体 provider 包类型，issue 明确要求）
    // ================================================================

    [Fact]
    public void GivenSqlServerProviderName_WhenProviderResolvesLockType_ThenReturnsSqlServerLockType()
    {
        // Arrange — 模拟 SqlServer ProviderName
        var providerName = "Microsoft.EntityFrameworkCore.SqlServer";

        // Act
        var resolved = OutboxLockProvider.ResolveLockType(providerName);

        // Then — 命名匹配选择 SqlServerOutboxLock；不绑具体包类型
        resolved.Should().Be(typeof(SqlServerOutboxLock<>),
            "ProviderName 包含 'Microsoft.EntityFrameworkCore.SqlServer' 时必须解析为 SqlServerOutboxLock<TContext> 开放类型");
    }

    [Fact]
    public void GivenPostgresProviderName_WhenProviderResolvesLockType_ThenReturnsPostgresLockType()
    {
        // Arrange — 模拟 PostgreSQL ProviderName
        var providerName = "Npgsql.EntityFrameworkCore.PostgreSQL";

        // Act
        var resolved = OutboxLockProvider.ResolveLockType(providerName);

        // Then
        resolved.Should().Be(typeof(PostgresOutboxLock<>),
            "ProviderName 包含 'Npgsql.EntityFrameworkCore.PostgreSQL' 时必须解析为 PostgresOutboxLock<TContext> 开放类型");
    }

    [Fact]
    public void GivenInMemoryProviderName_WhenProviderResolvesLockType_ThenReturnsNoOpLockType()
    {
        // Arrange — InMemory 不被支持，必须回退 NoOp（避免破坏 0/1 实例部署）
        var providerName = "Microsoft.EntityFrameworkCore.InMemory";

        // Act
        var resolved = OutboxLockProvider.ResolveLockType(providerName);

        // Then
        resolved.Should().Be<NoOpOutboxLock>(
            "ProviderName 为 InMemory 时必须回退 NoOpOutboxLock — InMemory 不支持 SQL 提示，无法跑真实行级锁");
    }

    [Fact]
    public void GivenUnknownProviderName_WhenProviderResolvesLockType_ThenReturnsNoOpLockType()
    {
        // Arrange — 任何未知 ProviderName 都不能抛异常
        var providerName = "SomeUnknown.Provider.Name";

        // Act
        var resolved = OutboxLockProvider.ResolveLockType(providerName);

        // Then — 命名匹配，未知 provider 必须回退到 NoOp，绝不抛异常
        resolved.Should().Be<NoOpOutboxLock>(
            "未知的 ProviderName 必须回退到 NoOpOutboxLock — 命名匹配是开放集合，不抛异常");
    }

    [Fact]
    public void GivenNullOrEmptyProviderName_WhenProviderResolvesLockType_ThenReturnsNoOpLockType()
    {
        // Arrange + Act — 空 ProviderName 是未配置 DbContext 的常见场景
        var resolvedNull = OutboxLockProvider.ResolveLockType(null);
        var resolvedEmpty = OutboxLockProvider.ResolveLockType(string.Empty);

        // Then
        resolvedNull.Should().Be<NoOpOutboxLock>(
            "null ProviderName 必须回退 NoOp — 不能抛 ArgumentNullException 破坏 Relay 启动");
        resolvedEmpty.Should().Be<NoOpOutboxLock>(
            "空串 ProviderName 必须回退 NoOp");
    }

    // ================================================================
    // Scenario 3: SqlServerOutboxLock — Happy Path
    //   单元测试用 InMemory 验证 LockedUntil / LockedByInstance 写入（issue 明确要求）
    // ================================================================

    [Fact]
    public async Task GivenSqlServerLockAndUnlockedMessage_WhenTryAcquire_ThenLockFieldsAreWritten()
    {
        // Arrange — InMemory provider 跑 happy path，issue 明确要求此口径
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateInMemoryFactory(dbName);
        var msgId = await SeedAsync(factory, NewMessage("E", DateTimeOffset.UtcNow));

        IOutboxLock sut = new SqlServerOutboxLock<TestDbContext>(factory);

        // Act
        var acquired = await sut.TryAcquireAsync(
            messageId: msgId,
            instanceId: "instance-A",
            lease: TimeSpan.FromMinutes(1),
            cancellationToken: CancellationToken.None);

        // Then — 拿锁成功 + 行状态写入
        acquired.Should().BeTrue(
            "未被任何实例持有的消息，本实例应当能拿到锁");
        var reloaded = await ReloadAsync(factory, msgId);
        reloaded.Should().NotBeNull();
        reloaded!.LockedByInstance.Should().Be(
            "instance-A",
            "TryAcquire 必须写入持有者实例 ID，便于所有权判断");
        reloaded.LockedUntil.Should().NotBeNull(
            "TryAcquire 必须写入 LockedUntil，作为租约到期判定依据");
        reloaded.LockedUntil!.Value.Should().BeAfter(
            DateTimeOffset.UtcNow.AddSeconds(-1),
            "LockedUntil 必须指向未来时刻，标识租约期内有效");
    }

    [Fact]
    public async Task GivenSqlServerLockAndHeldMessage_WhenTryAcquireByOtherInstance_ThenReturnsFalse()
    {
        // Arrange — instance-B 已经持有，A 再来抢应失败
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateInMemoryFactory(dbName);
        var msgId = await SeedAsync(factory, NewMessage("E", DateTimeOffset.UtcNow));

        IOutboxLock sut = new SqlServerOutboxLock<TestDbContext>(factory);
        await sut.TryAcquireAsync(msgId, "instance-B", TimeSpan.FromMinutes(1), CancellationToken.None);

        // Act
        var acquired = await sut.TryAcquireAsync(msgId, "instance-A", TimeSpan.FromMinutes(1), CancellationToken.None);

        // Then — 别人持锁时本实例必须跳过
        acquired.Should().BeFalse(
            "其他实例仍持有锁时，本实例 TryAcquire 必须返回 false — 这是 #74 已知风险 #1 的核心防护");
    }

    [Fact]
    public async Task GivenSqlServerLockAndExpiredLease_WhenTryAcquire_ThenSucceeds()
    {
        // Arrange — LockedUntil 已在过去 (锁已过期)，任何实例可重新拾取
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateInMemoryFactory(dbName);
        var msgId = await SeedAsync(factory, NewMessage(
            "E",
            DateTimeOffset.UtcNow,
            lockedByInstance: "instance-old",
            lockedUntil: DateTimeOffset.UtcNow.AddSeconds(-1)));

        IOutboxLock sut = new SqlServerOutboxLock<TestDbContext>(factory);

        // Act
        var acquired = await sut.TryAcquireAsync(msgId, "instance-A", TimeSpan.FromMinutes(1), CancellationToken.None);

        // Then — 锁过期 = 任何实例可重新拾取
        acquired.Should().BeTrue(
            "租约到期后 (LockedUntil &lt;= now) 锁自动失效，任何实例 TryAcquire 必须能拿到锁");
        var reloaded = await ReloadAsync(factory, msgId);
        reloaded!.LockedByInstance.Should().Be(
            "instance-A",
            "新实例必须覆盖旧的持有者标识，便于排障");
    }

    [Fact]
    public async Task GivenSqlServerLockAndOwnedMessage_WhenRelease_ThenLockFieldsAreCleared()
    {
        // Arrange — 本实例持有，正常 Release
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateInMemoryFactory(dbName);
        var msgId = await SeedAsync(factory, NewMessage("E", DateTimeOffset.UtcNow));

        IOutboxLock sut = new SqlServerOutboxLock<TestDbContext>(factory);
        await sut.TryAcquireAsync(msgId, "instance-A", TimeSpan.FromMinutes(1), CancellationToken.None);

        // Act
        await sut.ReleaseAsync(msgId, "instance-A", CancellationToken.None);

        // Then — Release 必须清空 LockedUntil / LockedByInstance
        var reloaded = await ReloadAsync(factory, msgId);
        reloaded!.LockedUntil.Should().BeNull(
            "Release 必须清空 LockedUntil，让其他实例下轮可拾取");
        reloaded.LockedByInstance.Should().BeNull(
            "Release 必须清空 LockedByInstance，避免误把行标记为'仍被处理中'");
    }

    [Fact]
    public async Task GivenSqlServerLockAndMessageHeldByOtherInstance_WhenRelease_ThenDoesNotClear()
    {
        // Arrange — instance-B 持有，A 调 Release 必须不能误释放（所有权校验）
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateInMemoryFactory(dbName);
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
            "所有权校验：仅当 LockedByInstance == 调用方 instanceId 时才清空；否则不能误释放他人的锁");
    }

    [Fact]
    public async Task GivenSqlServerLockAndMissingMessage_WhenTryAcquire_ThenReturnsFalse()
    {
        // Arrange — messageId 不存在
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateInMemoryFactory(dbName);

        IOutboxLock sut = new SqlServerOutboxLock<TestDbContext>(factory);

        // Act
        var acquired = await sut.TryAcquireAsync(
            messageId: "non-existent-id",
            instanceId: "instance-A",
            lease: TimeSpan.FromMinutes(1),
            cancellationToken: CancellationToken.None);

        // Then — 行不存在时返回 false，不抛异常
        acquired.Should().BeFalse(
            "行不存在时 TryAcquire 必须返回 false — 不能因 'no row affected' 抛异常");
    }

    // ================================================================
    // Scenario 4: PostgresOutboxLock — Happy Path（与 SqlServer 语义对偶）
    // ================================================================

    [Fact]
    public async Task GivenPostgresLockAndUnlockedMessage_WhenTryAcquire_ThenLockFieldsAreWritten()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateInMemoryFactory(dbName);
        var msgId = await SeedAsync(factory, NewMessage("E", DateTimeOffset.UtcNow));

        IOutboxLock sut = new PostgresOutboxLock<TestDbContext>(factory);

        // Act
        var acquired = await sut.TryAcquireAsync(
            messageId: msgId,
            instanceId: "instance-A",
            lease: TimeSpan.FromMinutes(1),
            cancellationToken: CancellationToken.None);

        // Then — Postgres 实现必须满足与 SqlServer 完全一致的契约
        acquired.Should().BeTrue("Postgres 实现必须满足 IOutboxLock 契约：未被锁的行本实例必须能拿到");
        var reloaded = await ReloadAsync(factory, msgId);
        reloaded!.LockedByInstance.Should().Be("instance-A");
        reloaded.LockedUntil.Should().NotBeNull();
        reloaded.LockedUntil!.Value.Should().BeAfter(DateTimeOffset.UtcNow.AddSeconds(-1));
    }

    [Fact]
    public async Task GivenPostgresLockAndHeldMessage_WhenTryAcquireByOtherInstance_ThenReturnsFalse()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateInMemoryFactory(dbName);
        var msgId = await SeedAsync(factory, NewMessage("E", DateTimeOffset.UtcNow));

        IOutboxLock sut = new PostgresOutboxLock<TestDbContext>(factory);
        await sut.TryAcquireAsync(msgId, "instance-B", TimeSpan.FromMinutes(1), CancellationToken.None);

        // Act
        var acquired = await sut.TryAcquireAsync(msgId, "instance-A", TimeSpan.FromMinutes(1), CancellationToken.None);

        // Then
        acquired.Should().BeFalse(
            "Postgres 实现必须遵守契约：他人持锁时本实例必须跳过，避免 Relay 重复投递");
    }

    [Fact]
    public async Task GivenPostgresLockAndOwnedMessage_WhenRelease_ThenLockFieldsAreCleared()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateInMemoryFactory(dbName);
        var msgId = await SeedAsync(factory, NewMessage("E", DateTimeOffset.UtcNow));

        IOutboxLock sut = new PostgresOutboxLock<TestDbContext>(factory);
        await sut.TryAcquireAsync(msgId, "instance-A", TimeSpan.FromMinutes(1), CancellationToken.None);

        // Act
        await sut.ReleaseAsync(msgId, "instance-A", CancellationToken.None);

        // Then
        var reloaded = await ReloadAsync(factory, msgId);
        reloaded!.LockedUntil.Should().BeNull();
        reloaded.LockedByInstance.Should().BeNull();
    }

    [Fact]
    public async Task GivenPostgresLockAndMessageHeldByOtherInstance_WhenRelease_ThenDoesNotClear()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateInMemoryFactory(dbName);
        var msgId = await SeedAsync(factory, NewMessage("E", DateTimeOffset.UtcNow));

        IOutboxLock sut = new PostgresOutboxLock<TestDbContext>(factory);
        await sut.TryAcquireAsync(msgId, "instance-B", TimeSpan.FromMinutes(1), CancellationToken.None);

        // Act
        Func<Task> act = () => sut.ReleaseAsync(msgId, "instance-A", CancellationToken.None);

        // Then — 所有权校验同样适用 Postgres 实现
        await act.Should().NotThrowAsync();
        var reloaded = await ReloadAsync(factory, msgId);
        reloaded!.LockedByInstance.Should().Be(
            "instance-B",
            "Postgres 实现必须同样遵守所有权校验：仅当 LockedByInstance == 调用方 instanceId 时才清空");
    }

    [Fact]
    public async Task GivenPostgresLockAndMissingMessage_WhenTryAcquire_ThenReturnsFalse()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateInMemoryFactory(dbName);

        IOutboxLock sut = new PostgresOutboxLock<TestDbContext>(factory);

        // Act
        var acquired = await sut.TryAcquireAsync(
            messageId: "non-existent-id",
            instanceId: "instance-A",
            lease: TimeSpan.FromMinutes(1),
            cancellationToken: CancellationToken.None);

        // Then
        acquired.Should().BeFalse(
            "行不存在时 Postgres 实现也必须返回 false — 行为必须与 SqlServer 一致");
    }

    // ================================================================
    // Scenario 5: DI — LockProvider 选项与 ProviderName 共同决定注入实例
    // ================================================================

    [Fact]
    public void GivenLockProviderNone_WhenResolvingIOutboxLock_ThenNoOpInstanceIsReturned()
    {
        // Arrange — LockProvider=None 不论底层 provider 都返回 NoOp
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<TestDbContext>>(CreateInMemoryFactory(Guid.NewGuid().ToString("N")));
        services.AddSingleton(Options.Create(new OutboxRelayOptions { LockProvider = OutboxLockProviderKind.None }));
        services.AddOutboxRowLock<TestDbContext>();

        // Act
        var sp = services.BuildServiceProvider();
        var lockObj = sp.GetRequiredService<IOutboxLock>();

        // Then
        lockObj.Should().BeOfType<NoOpOutboxLock>(
            "LockProvider=None 时必须返回 NoOpOutboxLock — 默认行为，绝不引入 provider 依赖");
    }

    [Fact]
    public void GivenLockProviderRowLockAndSqlServerProvider_WhenResolvingIOutboxLock_ThenSqlServerLockIsReturned()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<TestDbContext>>(CreateInMemoryFactory(Guid.NewGuid().ToString("N")));
        services.AddSingleton(Options.Create(new OutboxRelayOptions { LockProvider = OutboxLockProviderKind.RowLock }));
        services.AddOutboxRowLock<TestDbContext>();

        // Act — Resolver 接受纯字符串 ProviderName，不依赖 DbContext 实例的可写属性
        var sp = services.BuildServiceProvider();
        var resolver = sp.GetRequiredService<IOutboxRowLockResolver<TestDbContext>>();
        var lockObj = resolver.Resolve(providerName: "Microsoft.EntityFrameworkCore.SqlServer");

        // Then — 选项 + ProviderName 共同决定
        lockObj.Should().BeOfType<SqlServerOutboxLock<TestDbContext>>(
            "LockProvider=RowLock + SqlServer ProviderName 必须解析为 SqlServerOutboxLock<TContext>");
    }

    [Fact]
    public void GivenLockProviderRowLockAndPostgresProvider_WhenResolvingIOutboxLock_ThenPostgresLockIsReturned()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<TestDbContext>>(CreateInMemoryFactory(Guid.NewGuid().ToString("N")));
        services.AddSingleton(Options.Create(new OutboxRelayOptions { LockProvider = OutboxLockProviderKind.RowLock }));
        services.AddOutboxRowLock<TestDbContext>();

        // Act
        var sp = services.BuildServiceProvider();
        var resolver = sp.GetRequiredService<IOutboxRowLockResolver<TestDbContext>>();
        var lockObj = resolver.Resolve(providerName: "Npgsql.EntityFrameworkCore.PostgreSQL");

        // Then
        lockObj.Should().BeOfType<PostgresOutboxLock<TestDbContext>>(
            "LockProvider=RowLock + Postgres ProviderName 必须解析为 PostgresOutboxLock<TContext>");
    }

    [Fact]
    public void GivenLockProviderDistributed_WhenResolvingIOutboxLock_ThenNoOpIsReturnedAsFallback()
    {
        // Arrange — Distributed 不在本任务范围，本任务要求 LockProvider=Distributed 时回退 NoOp（避免运行时 NRE）
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<TestDbContext>>(CreateInMemoryFactory(Guid.NewGuid().ToString("N")));
        services.AddSingleton(Options.Create(new OutboxRelayOptions { LockProvider = OutboxLockProviderKind.Distributed }));
        services.AddOutboxRowLock<TestDbContext>();

        // Act
        var sp = services.BuildServiceProvider();
        var resolver = sp.GetRequiredService<IOutboxRowLockResolver<TestDbContext>>();
        var lockObj = resolver.Resolve(providerName: "Microsoft.EntityFrameworkCore.SqlServer");

        // Then — 本任务只实现 RowLock 路径；Distributed 显式回退 NoOp 而非抛异常
        lockObj.Should().BeOfType<NoOpOutboxLock>(
            "LockProvider=Distributed 在本任务未实现时必须回退 NoOp — 绝不允许抛异常破坏运行时");
    }

    [Fact]
    public void GivenAddOutboxLockingWithoutAddOutboxRowLock_WhenResolvingIOutboxLock_ThenNoOpIsRegisteredByDefault()
    {
        // Arrange — 不调用 AddOutboxRowLock 时不应破坏 #80 默认行为
        var services = new ServiceCollection();
        services.AddOutboxLocking();

        // Act
        var sp = services.BuildServiceProvider();
        var lockObj = sp.GetRequiredService<IOutboxLock>();

        // Then — 向后兼容：未启用 RowLock 时仍是 NoOp
        lockObj.Should().BeOfType<NoOpOutboxLock>(
            "未显式 AddOutboxRowLock 时必须保留 #80 默认 NoOp 行为 — 向后兼容，不能强制要求所有用户启用 RowLock");
    }

    // ================================================================
    // Scenario 6: IOutboxRowLockResolver 契约 — 与 LockProvider 选项解耦
    // ================================================================

    [Fact]
    public void GivenAnyLockProvider_WhenResolvingWithInMemoryProviderName_ThenNoOpIsAlwaysReturned()
    {
        // Arrange — 即便 LockProvider=RowLock，InMemory provider 也必须解析为 NoOp（InMemory 不支持 SQL 提示）
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<TestDbContext>>(CreateInMemoryFactory(Guid.NewGuid().ToString("N")));
        services.AddSingleton(Options.Create(new OutboxRelayOptions { LockProvider = OutboxLockProviderKind.RowLock }));
        services.AddOutboxRowLock<TestDbContext>();

        // Act
        var sp = services.BuildServiceProvider();
        var resolver = sp.GetRequiredService<IOutboxRowLockResolver<TestDbContext>>();
        var lockObj = resolver.Resolve(providerName: "Microsoft.EntityFrameworkCore.InMemory");

        // Then — ProviderName 优先级高于 LockProvider：未知/InMemory 必须 NoOp
        lockObj.Should().BeOfType<NoOpOutboxLock>(
            "LockProvider=RowLock 但底层是 InMemory 时必须回退 NoOp — InMemory 无法支持 SQL 提示");
    }

    // ================================================================
    // Scenario 7: Step 4/6 — AddOutboxLocking<TContext> switch 表达式分派 Distributed / Leader
    //   原 switch 把"非 RowLock 一律 NoOp" — 本步扩展为：
    //   - LockProvider=Distributed → DistributedOutboxLock (IMultiLevelCache + IOptions<OutboxRelayOptions>)
    //   - LockProvider=Leader      → LeaderElector (IMultiLevelCache + IAtomicCounter + IOptions<OutboxRelayOptions>)
    //   复用 AddTenE0Caching() 注册的 IMultiLevelCache + IAtomicCounter，生产代码无需新增 service 注册。
    //   测试需要给 IDistributedCache 注册一个 in-memory 默认实现让 DI 能解析 cache + counter。
    // ================================================================

    private static IServiceCollection CreateDomainEventsServiceCollection(OutboxLockProviderKind kind)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddEntityFrameworkInMemoryDatabase();
        services.AddDbContextFactory<TestDbContext>((_, o) => o.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        services.AddSingleton<IDistributedCache, MemoryDistributedCache>();
        services.AddTenE0Caching();
        services.Configure<OutboxRelayOptions>(o => o.LockProvider = kind);
        return services;
    }

    [Fact]
    public void GivenLockProviderDistributed_WhenResolvingIOutboxLockFromAddOutboxLocking_ThenDistributedOutboxLockIsReturned()
    {
        // Arrange — 走 AddTenE0DomainEvents 真实集成路径（内部调 AddOutboxLocking<TContext>）
        //   而非 AddOutboxRowLock：plan 强调"Provider 探测仍走 switch 表达式"，
        //   新分支必须由 AddOutboxLocking 的内联 switch 解析。
        var services = CreateDomainEventsServiceCollection(OutboxLockProviderKind.Distributed);

        // Act
        services.AddTenE0DomainEvents<TestDbContext>();
        using var sp = services.BuildServiceProvider();
        var lockObj = sp.GetRequiredService<IOutboxLock>();

        // Then — Step 4/6：Distributed 分支必须解析到 DistributedOutboxLock 实例（不再回退 NoOp）
        lockObj.Should().BeOfType<DistributedOutboxLock>(
            "LockProvider=Distributed 时 AddOutboxLocking<TContext> 的 switch 必须解析为 DistributedOutboxLock — "
            + "feature #82 扩展 plan 4/6 要求把 NoOp 回退替换为真实注册，复用现有 IMultiLevelCache DI 注册");
    }

    [Fact]
    public void GivenLockProviderLeader_WhenResolvingIOutboxLockFromAddOutboxLocking_ThenLeaderElectorIsReturned()
    {
        // Arrange — 走 AddTenE0DomainEvents 真实集成路径；Leader 模式需要 IMultiLevelCache + IAtomicCounter
        var services = CreateDomainEventsServiceCollection(OutboxLockProviderKind.Leader);

        // Act
        services.AddTenE0DomainEvents<TestDbContext>();
        using var sp = services.BuildServiceProvider();
        var lockObj = sp.GetRequiredService<IOutboxLock>();

        // Then — Step 4/6：Leader 分支必须解析到 LeaderElector 实例
        lockObj.Should().BeOfType<LeaderElector>(
            "LockProvider=Leader 时 AddOutboxLocking<TContext> 的 switch 必须解析为 LeaderElector — "
            + "feature #82 扩展 plan 4/6 要求把 NoOp 回退替换为真实注册，复用现有 IMultiLevelCache + IAtomicCounter DI 注册");
    }
}
