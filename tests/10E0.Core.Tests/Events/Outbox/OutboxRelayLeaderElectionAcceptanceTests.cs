using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TenE0.Core.Caching;
using TenE0.Core.Events.Outbox;
using TenE0.Core.Tests.Events.Outbox.TestFakes;

namespace TenE0.Core.Tests.Events.Outbox;

/// <summary>
/// BDD 验收测试 — Outbox "全局 Relay leader election" 备选路径 (#82 / #74 子任务)
///
/// <para>
/// 业务动机：
/// 行级锁 + 应用层锁都解决"单条消息"的并发抢占。另一种思路是"全局一次只让一个 Relay
/// 实例跑全流程"（leader election）— leader 拿到租约前不投递任何消息，从根上消除竞争。
/// 适合：消息量小但部署实例多的场景，避免每次都要拿 N 把行级锁的开销。
/// </para>
///
/// <para>
/// 验收口径：
/// - <see cref="OutboxLockProviderKind"/> 新增 <c>Leader</c> 枚举值（与 <c>RowLock</c> / <c>Distributed</c> 并列）；
/// - Leader 模式复用 <see cref="IMultiLevelCache"/> L2 + <see cref="IAtomicCounter"/> 做租约式 leader lock；
/// - 同一时刻全集群仅一个实例持有 leader 租约（其他实例的 <c>IsLeaderAsync</c> 返回 false）；
/// - 租约到期后其他实例能接管（failover）；
/// - Leader 的心跳续约必须延长租约（leader 不应被自己误失效）。
/// </para>
///
/// <para>
/// 不验证：
/// - 真实 Redis 集群下的 SPLIT-BRAIN（依赖 Redis 自身租约 + 时钟同步）
/// - 跨机房 leader 选举延迟（依赖业务层机房亲和性）
/// </para>
/// </summary>
[Trait("Category", "Acceptance")]
public sealed class OutboxRelayLeaderElectionAcceptanceTests
{
    // ================================================================
    // Test infrastructure — 用共享 TestFakes helper（#82 PR #88 bot review
    // 揭示本地 fake 类在 4 个文件重复且缺 TrySetAsync/SetAsync 实现）
    // ================================================================

    private static (IMultiLevelCache cache, InMemoryDistributedCache l2) CreateCache()
    {
        var l1 = new MemoryCache(new MemoryCacheOptions());
        var l2 = new InMemoryDistributedCache();
        var cache = new L1L2CacheForTest(l1, l2);
        return (cache, l2);
    }

    private static LeaderElector CreateElector(
        IMultiLevelCache cache,
        string instanceId,
        TimeSpan? lease = null)
    {
        var options = Options.Create(new OutboxRelayOptions
        {
            LockInstanceId = instanceId,
            LockLeaseDuration = lease ?? TimeSpan.FromSeconds(30),
        });
        return new LeaderElector(cache, options);
    }

    // ================================================================
    // Scenario 1: 枚举 — Leader 模式是 OutboxLockProviderKind 的新并列值
    // ================================================================

    [Fact]
    public void GivenOutboxLockProviderKind_WhenEnumerated_ThenLeaderValueExists()
    {
        // Arrange + Act — 枚举值必须存在
        var names = Enum.GetNames<OutboxLockProviderKind>();

        // Then — Leader 必须与 None / RowLock / Distributed 并列
        names.Should().Contain("Leader",
            "OutboxLockProviderKind 必须新增 Leader 枚举值 — issue #82 明确要求与 RowLock/Distributed 并列分发");
    }

    [Fact]
    public void GivenOutboxRelayOptions_WhenLockProviderSetToLeader_ThenValueIsExposed()
    {
        // Arrange
        var options = new OutboxRelayOptions { LockProvider = OutboxLockProviderKind.Leader };

        // Then
        options.LockProvider.Should().Be(OutboxLockProviderKind.Leader,
            "LockProvider 必须可显式配置为 Leader 以启用 leader election 模式");
    }

    // ================================================================
    // Scenario 2: 类型契约 — LeaderElector 实现 IOutboxLock
    //   leader election 是 IOutboxLock 的另一种实现思路：
    //   TryAcquire = "尝试成为 leader"；Release = "放弃 leader 身份"
    // ================================================================

    [Fact]
    public void GivenLeaderElector_WhenConstructed_ThenImplementsIOutboxLock()
    {
        // Arrange
        var (cache, _) = CreateCache();
        var sut = CreateElector(cache, "instance-A");

        // Then
        sut.Should().BeAssignableTo<IOutboxLock>(
            "LeaderElector 是 IOutboxLock 的另一种实现 — leader 拿到租约 = 这一轮 Relay 跑全流程；"
            + "没拿到 = 这一轮整个实例跳过所有消息");
    }

    // ================================================================
    // Scenario 3: 首个实例当选 — 无人是 leader 时本实例必须当选
    // ================================================================

    [Fact]
    public async Task GivenNoLeader_WhenTryAcquire_ThenThisInstanceBecomesLeader()
    {
        // Arrange — 全新缓存 + 计数器
        var (cache, _) = CreateCache();
        IOutboxLock sut = CreateElector(cache, "instance-A");

        // Act
        var acquired = await sut.TryAcquireAsync(
            messageId: "*", // leader 模式 messageId 通配（leader 是全局概念，不是行级）
            instanceId: "instance-A",
            lease: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);

        // Then — 无人是 leader 时本实例当选
        acquired.Should().BeTrue(
            "无人持有 leader 租约时本实例必须能当选 leader — 这是 leader election 的预期主路径");
    }

    // ================================================================
    // Scenario 4: 同实例续约 — leader 续任必须返回 true
    // ================================================================

    [Fact]
    public async Task GivenSelfIsLeader_WhenTryAcquireAgain_ThenRenewsLeadership()
    {
        // Arrange — 先让 A 当选
        var (cache, _) = CreateCache();
        IOutboxLock sut = CreateElector(cache, "instance-A");
        await sut.TryAcquireAsync("*", "instance-A", TimeSpan.FromSeconds(30), CancellationToken.None);

        // Act — 同实例续约
        var renewed = await sut.TryAcquireAsync(
            messageId: "*",
            instanceId: "instance-A",
            lease: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);

        // Then — 续任必须成功（leader 心跳续租）
        renewed.Should().BeTrue(
            "已是 leader 的实例再次 TryAcquire 必须返回 true — 续租是 leader 心跳的核心语义");
    }

    // ================================================================
    // Scenario 5: 互斥 — 其他实例 TryAcquire 必须返回 false
    // ================================================================

    [Fact]
    public async Task GivenOtherInstanceIsLeader_WhenTryAcquireBySelf_ThenReturnsFalse()
    {
        // Arrange — A 已当选
        var (cache, _) = CreateCache();
        var sutA = CreateElector(cache, "instance-A");
        IOutboxLock sutB = CreateElector(cache, "instance-B");
        await sutA.TryAcquireAsync("*", "instance-A", TimeSpan.FromSeconds(30), CancellationToken.None);

        // Act — B 来抢
        var acquired = await sutB.TryAcquireAsync(
            messageId: "*",
            instanceId: "instance-B",
            lease: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);

        // Then — B 必须跳过
        acquired.Should().BeFalse(
            "A 已当选 leader 时 B TryAcquire 必须返回 false — 同一时刻全集群仅一个 leader");
    }

    // ================================================================
    // Scenario 6: Release — leader 主动放弃后其他实例能接管
    // ================================================================

    [Fact]
    public async Task GivenLeaderReleases_WhenOtherInstanceAcquires_ThenTakesOverLeadership()
    {
        // Arrange — A 当选
        var (cache, _) = CreateCache();
        var sutA = CreateElector(cache, "instance-A");
        var sutB = CreateElector(cache, "instance-B");
        await sutA.TryAcquireAsync("*", "instance-A", TimeSpan.FromSeconds(30), CancellationToken.None);

        // Act — A 主动 Release
        await sutA.ReleaseAsync("*", "instance-A", CancellationToken.None);

        // Then — B 必须能接管
        var bAcquired = await sutB.TryAcquireAsync(
            messageId: "*",
            instanceId: "instance-B",
            lease: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);
        bAcquired.Should().BeTrue(
            "原 leader 主动 Release 后其他实例必须能接管 — 这是 leader failover 的预期主路径");
    }

    // ================================================================
    // Scenario 7: 所有权校验 — 非 leader 调 Release 不能误放弃他人 leader 身份
    // ================================================================

    [Fact]
    public async Task GivenOtherInstanceIsLeader_WhenReleaseBySelf_ThenDoesNotClear()
    {
        // Arrange — A 是 leader
        var (cache, _) = CreateCache();
        var sutA = CreateElector(cache, "instance-A");
        var sutB = CreateElector(cache, "instance-B");
        await sutA.TryAcquireAsync("*", "instance-A", TimeSpan.FromSeconds(30), CancellationToken.None);

        // Act — B 误调 Release
        Func<Task> act = () => sutB.ReleaseAsync("*", "instance-B", CancellationToken.None);

        // Then — 不抛异常 + A 仍为 leader
        await act.Should().NotThrowAsync(
            "Release 必须幂等；非 leader 实例 Release 必须不抛异常");
        // 验证 A 仍是 leader：A 再续约应成功
        var aReNewed = await sutA.TryAcquireAsync(
            messageId: "*",
            instanceId: "instance-A",
            lease: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);
        aReNewed.Should().BeTrue(
            "B 误 Release 不能动 A 的 leader 身份 — 所有权校验是 leader election 的安全护栏");
    }

    // ================================================================
    // Scenario 8: DI 选型 — LockProvider=Leader 必须解析为 LeaderElector
    // ================================================================

    [Fact]
    public void GivenLockProviderLeader_WhenResolvingIOutboxLock_ThenLeaderElectorIsReturned()
    {
        // Arrange — DI 模拟：LockProvider=Leader 时走 LeaderElector
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var l1 = new MemoryCache(new MemoryCacheOptions());
        var l2 = new InMemoryDistributedCache();
        services.AddSingleton<IMemoryCache>(l1);
        services.AddSingleton<IDistributedCache>(l2);
        services.AddSingleton<IMultiLevelCache, L1L2CacheForTest>();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(
            new OutboxRelayOptions
            {
                LockProvider = OutboxLockProviderKind.Leader,
                LockInstanceId = "instance-A",
            }));
        services.AddSingleton<IOutboxLock>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OutboxRelayOptions>>().Value;
            return opts.LockProvider == OutboxLockProviderKind.Leader
                ? new LeaderElector(
                    sp.GetRequiredService<IMultiLevelCache>(),
                    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OutboxRelayOptions>>())
                : new NoOpOutboxLock();
        });

        // Act
        var sp = services.BuildServiceProvider();
        var lockObj = sp.GetRequiredService<IOutboxLock>();

        // Then
        lockObj.Should().BeOfType<LeaderElector>(
            "LockProvider=Leader 必须解析为 LeaderElector — 走 leader election 路径");
    }
}
