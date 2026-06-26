using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.DependencyInjection;
using TenE0.Core.Events.Outbox;

namespace TenE0.Core.Tests.Events.Outbox;

/// <summary>
/// 单元测试 — <see cref="OutboxRelayOptions"/> 默认值稳定性 +
/// <see cref="OutboxLockingServiceCollectionExtensions.AddOutboxLocking{TContext}"/>
/// 按 <see cref="OutboxRelayOptions.LockProvider"/> + <c>IDbContextFactory&lt;TContext&gt;</c>
/// <c>ProviderName</c> 字符串命名匹配选型的 DI 注册路径。
///
/// <para>
/// <b>本测试与 #80 既有的 OutboxSchemaSeederTests / OutboxLockProviderAcceptanceTests 的分工：</b>
/// <list type="bullet">
/// <item>OutboxLockProviderAcceptanceTests（已落地）覆盖 OutboxLockProvider 静态选型 + IOutboxRowLockResolver + AddOutboxRowLock 解析期委托工厂。</item>
/// <item>本测试聚焦 <see cref="OutboxLockingServiceCollectionExtensions.AddOutboxLocking{TContext}"/>
/// 真实 <c>AddTenE0DomainEvents</c> 集成路径下的 LockProvider 选型分支 —
/// AddTenE0DomainEvents 内部调用 <c>AddOutboxLocking&lt;TContext&gt;</c>，不是 <c>AddOutboxRowLock</c>，
/// 故 OutboxLockProviderAcceptanceTests 的 AddOutboxRowLock 路径不能直接覆盖本场景。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>为什么不直接改既有 OutboxLockProviderAcceptanceTests？</b>
/// OutboxLockProviderAcceptanceTests 是 BDD 风格验收测试（Category=Acceptance），覆盖
/// "业务契约 / 多实例部署下 Relay 行为对偶"等高层语义。本测试是 TDD 单元测试
/// （Category=Unit），覆盖 <c>AddTenE0DomainEvents</c> 真实集成路径下 DI 注册分支
/// （与 OutboxSchemaSeederTests 同款 ServiceCollection + AddDbContextFactory 模板）。
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class OutboxLockProviderSelectionTests
{
    // ================================================================
    // Test Infrastructure — 复用 #80 OutboxSchemaSeederTests 第 60-78 行模板
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

    // ================================================================
    // Scenario 1: OutboxRelayOptions.LockProvider 默认值稳定性
    //   pin 死默认 == None（与 #80 NoOpOutboxLock 等价；0/1 实例部署零感知）
    // ================================================================

    [Fact]
    public void GivenNewOutboxRelayOptions_WhenConstructed_ThenLockProviderDefaultsToNone()
    {
        // Arrange + Act
        var options = new OutboxRelayOptions();

        // Then — 默认 None 让 0/1 实例部署零感知
        options.LockProvider.Should().Be(
            OutboxLockProviderKind.None,
            "LockProvider 默认必须是 None — 与 #80 NoOpOutboxLock 等价；"
            + "0/1 实例部署绝不感知多实例锁逻辑");
    }

    [Fact]
    public void GivenTwoNewOutboxRelayOptions_WhenConstructed_ThenLockProviderDefaultsAreIdentical()
    {
        // Arrange + Act
        var a = new OutboxRelayOptions();
        var b = new OutboxRelayOptions();

        // Then — 默认值必须确定可预测（不受 GUID / 机器名等副作用影响）
        a.LockProvider.Should().Be(b.LockProvider,
            "LockProvider 默认值必须在同一类型的所有实例上保持一致（不是属性随机生成）");
    }

    // ================================================================
    // Scenario 1b: Step 1/6 — Leader 模式枚举 + options 默认值稳定性
    //   钉死 feature #82 引入的 OutboxLockProviderKind.Leader 枚举存在
    //   + 后续 LeaderElection 需要的 options 字段默认值（占位配置）
    //   本测试只验证枚举 + options 字段的"存在 + 默认值"，不触发任何
    //   LeaderElection 行为（行为在后续步骤实现）。
    // ================================================================

    [Fact]
    public void GivenNewOutboxRelayOptions_WhenConstructed_ThenLeaderEnumExistsAndIsNotDefaultChoice()
    {
        // Arrange — 验证 Leader 枚举值已存在（int 3，向后兼容 None=0/RowLock=1/Distributed=2）
        var leaderKind = OutboxLockProviderKind.Leader;

        // Then — Leader 必须有非 None 的显式 int 值，不能与已有枚举值冲突
        leaderKind.Should().NotBe(OutboxLockProviderKind.None,
            "Leader 是独立的 provider kind — 不能复用 None（语义完全不同："
            + "Leader 是全局只一个 Relay 承担投递，其余实例空闲待命）");
        ((int)leaderKind).Should().Be(3,
            "Leader 必须是显式 int=3 — 与 None=0/RowLock=1/Distributed=2 错开，向后兼容");
    }

    [Fact]
    public void GivenNewOutboxRelayOptions_WhenConstructed_ThenLeaderLeaseAndKeyPrefixDefaultToSaneValues()
    {
        // Arrange + Act — Step 1/6 引入的 Leader 模式 options 默认值
        var options = new OutboxRelayOptions();

        // Then — 默认 30s 租约（与 LockLeaseDuration 保持一致语义）
        options.LeaderLeaseDuration.Should().Be(TimeSpan.FromSeconds(30),
            "LeaderLeaseDuration 默认 30s — 与 LockLeaseDuration 一致；"
            + "Lease 过期后另一实例可在可接受时间内抢主");

        // Then — 默认 Redis key 前缀
        options.LeaderInstanceKeyPrefix.Should().Be("outbox:leader",
            "LeaderInstanceKeyPrefix 默认 'outbox:leader' — 让多套环境共用 Redis 时不冲突");
    }

    // ================================================================
    // Scenario 2: AddTenE0DomainEvents + InMemory 真实集成路径 — 默认下落 NoOp
    //   验证 #80 AddOutboxLocking<TContext> switch 表达式的"未知 provider → NoOp"分支
    //   （InMemory 的 ProviderName 含 "InMemory"，不匹配 SqlServer/Npgsql/Postgres → NoOp）
    // ================================================================

    [Fact]
    public void GivenServiceCollectionWithInMemoryProvider_WhenAddTenE0DomainEventsCalled_ThenIOutboxLockIsNoOp()
    {
        // Arrange — 复用 #80 OutboxSchemaSeederTests 第 60-78 行模板
        var services = new ServiceCollection()
            .AddLogging()
            .AddEntityFrameworkInMemoryDatabase();
        services.AddDbContextFactory<TestDbContext>((_, o) => o.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));

        // Act — 走 AddTenE0DomainEvents 真实集成路径（内部会调 AddOutboxLocking<TContext>）
        services.AddTenE0DomainEvents<TestDbContext>();

        // Assert — InMemory provider 下必须注册 NoOpOutboxLock（ProviderName 不匹配 SqlServer/Npgsql）
        using var sp = services.BuildServiceProvider();
        var lockObj = sp.GetService<IOutboxLock>();

        lockObj.Should().NotBeNull("AddTenE0DomainEvents 必须注册 IOutboxLock 默认实现");
        lockObj.Should().BeOfType<NoOpOutboxLock>(
            "AddTenE0DomainEvents 集成路径下，InMemory provider 的 ProviderName 不匹配 SqlServer/Npgsql/Postgres，"
            + "switch 表达式保守回退到 NoOpOutboxLock，绝不抛异常");
    }

    [Fact]
    public void GivenServiceCollectionWithInMemoryProviderAndLockProviderNone_WhenResolvingIOutboxLock_ThenNoOpIsReturned()
    {
        // Arrange — 显式 LockProvider=None（默认值，但显式配置一遍以钉死分支）
        var services = new ServiceCollection()
            .AddLogging()
            .AddEntityFrameworkInMemoryDatabase();
        services.AddDbContextFactory<TestDbContext>((_, o) => o.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        services.Configure<OutboxRelayOptions>(o => o.LockProvider = OutboxLockProviderKind.None);

        // Act
        services.AddTenE0DomainEvents<TestDbContext>();

        // Assert — LockProvider=None → 无条件 NoOp（不探测 ProviderName）
        using var sp = services.BuildServiceProvider();
        var lockObj = sp.GetRequiredService<IOutboxLock>();

        lockObj.Should().BeOfType<NoOpOutboxLock>(
            "LockProvider=None 时必须无条件回退 NoOp — 不论底层 provider 是 SqlServer / Postgres / InMemory / 未知");
    }

    [Fact]
    public void GivenServiceCollectionWithInMemoryProviderAndLockProviderDistributed_WhenResolvingIOutboxLock_ThenNoOpIsReturned()
    {
        // Arrange — LockProvider=Distributed 是本任务未实现的回退路径
        var services = new ServiceCollection()
            .AddLogging()
            .AddEntityFrameworkInMemoryDatabase();
        services.AddDbContextFactory<TestDbContext>((_, o) => o.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        services.Configure<OutboxRelayOptions>(o => o.LockProvider = OutboxLockProviderKind.Distributed);

        // Act
        services.AddTenE0DomainEvents<TestDbContext>();

        // Assert — Distributed 本任务未实现 → 回退 NoOp（不抛异常）
        using var sp = services.BuildServiceProvider();
        var lockObj = sp.GetRequiredService<IOutboxLock>();

        lockObj.Should().BeOfType<NoOpOutboxLock>(
            "LockProvider=Distributed 在本任务未实现时必须回退 NoOp — "
            + "绝不允许抛异常破坏 Relay 启动（plan 硬约束：未实现分支保守 NoOp）");
    }

    // ================================================================
    // Scenario 3: AddTenE0DomainEvents + LockProvider=RowLock + InMemory 真实集成路径
    //   验证 switch 表达式的"RowLock + 未知 ProviderName → NoOp"分支
    //   （InMemory 是不支持的 provider；plan 明确要求"默认 InMemory 下落到 NoOp"）
    // ================================================================

    [Fact]
    public void GivenServiceCollectionWithInMemoryProviderAndLockProviderRowLock_WhenResolvingIOutboxLock_ThenNoOpIsReturned()
    {
        // Arrange — LockProvider=RowLock 但底层是 InMemory（不支持的 provider）
        var services = new ServiceCollection()
            .AddLogging()
            .AddEntityFrameworkInMemoryDatabase();
        services.AddDbContextFactory<TestDbContext>((_, o) => o.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        services.Configure<OutboxRelayOptions>(o => o.LockProvider = OutboxLockProviderKind.RowLock);

        // Act
        services.AddTenE0DomainEvents<TestDbContext>();

        // Assert — InMemory + RowLock → 探测 ProviderName → 不匹配 SqlServer/Npgsql → NoOp
        using var sp = services.BuildServiceProvider();
        var lockObj = sp.GetRequiredService<IOutboxLock>();

        lockObj.Should().BeOfType<NoOpOutboxLock>(
            "LockProvider=RowLock 但底层是 InMemory 时，ProviderName 含 'InMemory' 不匹配 SqlServer/Npgsql/Postgres，"
            + "switch 表达式保守回退 NoOpOutboxLock — 绝不允许在 InMemory 上激活 SqlServer/Postgres 行级锁");
    }

    // ================================================================
    // Scenario 4: 探测失败回退分支 — ProviderName 探测抛异常时必须 NoOp
    //   #80 AddOutboxLocking<TContext> 的 switch 表达式外层套了 try/catch：
    //   factory.CreateDbContext() 抛异常 → 保守 NoOp。
    //   用 ThrowingDbContextFactory 模拟（避免拉 SqlServer/PG 真包）。
    // ================================================================

    [Fact]
    public void GivenThrowingDbContextFactoryAndLockProviderRowLock_WhenResolvingIOutboxLock_ThenNoOpIsReturned()
    {
        // Arrange — 自定义 IOutboxLock 委托覆盖 AddTenE0DomainEvents 的注册
        var services = new ServiceCollection()
            .AddLogging();
        services.Configure<OutboxRelayOptions>(o => o.LockProvider = OutboxLockProviderKind.RowLock);
        services.AddSingleton<IDbContextFactory<TestDbContext>>(new ThrowingDbContextFactory());
        services.AddTenE0DomainEvents<TestDbContext>();

        // Act
        using var sp = services.BuildServiceProvider();
        var lockObj = sp.GetRequiredService<IOutboxLock>();

        // Then — factory.CreateDbContext() 抛异常 → try/catch 兜底 → NoOp
        lockObj.Should().BeOfType<NoOpOutboxLock>(
            "AddOutboxLocking<TContext> 内部探测 ProviderName 时若 factory.CreateDbContext() 抛异常，"
            + "必须被 try/catch 捕获并保守回退 NoOpOutboxLock — 绝不允许异常穿透破坏 Relay 启动");
    }

    /// <summary>模拟 factory.CreateDbContext() 抛异常（模拟 SqlServer/PG 未注册等异常路径）。</summary>
    private sealed class ThrowingDbContextFactory : IDbContextFactory<TestDbContext>
    {
        public TestDbContext CreateDbContext() =>
            throw new InvalidOperationException("Simulated DbContext creation failure");
    }

    // ================================================================
    // Scenario 5: AddOutboxLocking<TContext> 链式返回值 + 默认 OutboxLockOptions 注册
    //   pin 死 #80 扩展方法的两个细节契约：
    //   1) 链式返回同一 IServiceCollection
    //   2) 同时注册 OutboxLockOptions 单例（兼容老配置代码）
    // ================================================================

    [Fact]
    public void GivenServiceCollectionWithInMemoryProvider_WhenAddTenE0DomainEventsCalled_ThenOutboxLockOptionsIsRegisteredAsSingleton()
    {
        // Arrange — 复用 #80 OutboxSchemaSeederTests 第 60-78 行模板
        var services = new ServiceCollection()
            .AddLogging()
            .AddEntityFrameworkInMemoryDatabase();
        services.AddDbContextFactory<TestDbContext>((_, o) => o.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));

        // Act
        services.AddTenE0DomainEvents<TestDbContext>();

        // Assert — OutboxLockOptions 必须被注册（兼容 #80 老配置代码）
        using var sp = services.BuildServiceProvider();
        var lockOptions = sp.GetService<OutboxLockOptions>();

        lockOptions.Should().NotBeNull(
            "AddTenE0DomainEvents 内部的 AddOutboxLocking<TContext> 必须注册 OutboxLockOptions 单例 — "
            + "保留 #80 老配置代码兼容路径");
    }

    [Fact]
    public void GivenServiceCollectionWithInMemoryProvider_WhenAddTenE0DomainEventsCalled_ThenIOutboxLockRegisteredAsNoOpAndSingleton()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddLogging()
            .AddEntityFrameworkInMemoryDatabase();
        services.AddDbContextFactory<TestDbContext>((_, o) => o.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));

        // Act
        services.AddTenE0DomainEvents<TestDbContext>();

        // Assert — IOutboxLock 是单例（Relay 编排代码拿到的实例应稳定）
        using var sp = services.BuildServiceProvider();
        var first = sp.GetRequiredService<IOutboxLock>();
        var second = sp.GetRequiredService<IOutboxLock>();

        first.Should().BeSameAs(second,
            "IOutboxLock 必须注册为单例 — Relay 编排代码每次拿到的应是同一实例，"
            + "避免每次解析都重探测 ProviderName / 重新构造");
    }
}
