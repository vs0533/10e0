using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TenE0.Core.Caching;
using TenE0.Core.Events.Outbox;
using TenE0.Core.Tests.Events.Outbox.TestFakes;

namespace TenE0.Core.Tests.Events.Outbox;

/// <summary>
/// BDD 验收测试 — 应用层 <c>DistributedOutboxLock</c> (#82 / #74 子任务)
///
/// <para>
/// 业务动机：
/// #80 落地的 <see cref="IOutboxLock"/> 抽象只覆盖"数据库行级锁"（SQL Server UPDLOCK/READPAST
/// 或 PG FOR UPDATE SKIP LOCKED）。但生产环境里很多团队没有跨库写锁，希望用分布式缓存
/// （Redis / SqlServer sp_getapplock 等）实现"行级 + 应用层"锁 — 复用
/// <see cref="IMultiLevelCache"/> 的 L2（<see cref="IDistributedCache"/>）做 token 存储。
/// </para>
///
/// <para>
/// 验收口径：
/// - key 命名空间：<c>outbox:lock:{messageId}</c>，value 为 instanceId；
/// - 过期时间 = 租约（lease）；
/// - TryAcquire 走"GetOrSet 风格的 compare-and-set"：本实例持有则续约；他人持有则返回 false；
/// - Release 必须做 L1 比对防误删（即便 L2 已过期被他人续约，本实例 Release 也不能清掉他人锁）；
/// - 多次调用本实例 TryAcquire 必须返回 true（续约语义）；
/// - 他实例 TryAcquire 必须返回 false（即便首次抢锁的实例已经存在）；
/// - DI 选项注册：<see cref="OutboxLockProviderKind.Distributed"/> 必须落到本实现（不是 NoOp）。
/// </para>
///
/// <para>
/// 不验证：
/// - 真实 Redis 集群 / SqlServer sp_getapplock 集成（属于 issue 后续运维验证范畴）
/// - 跨进程 Redis 时钟漂移（依赖 Redis 自身 TTL 精度）
/// </para>
/// </summary>
[Trait("Category", "Acceptance")]
public sealed class DistributedOutboxLockAcceptanceTests
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

    private static DistributedOutboxLock CreateLock(
        IMultiLevelCache cache,
        string instanceId = "instance-A",
        TimeSpan? lease = null)
    {
        var options = Options.Create(new OutboxRelayOptions
        {
            LockInstanceId = instanceId,
            LockLeaseDuration = lease ?? TimeSpan.FromSeconds(30),
        });
        return new DistributedOutboxLock(cache, options);
    }

    // ================================================================
    // Scenario 1: 类型契约 — DistributedOutboxLock 必须实现 IOutboxLock
    // ================================================================

    [Fact]
    public void GivenDistributedOutboxLock_WhenConstructed_ThenImplementsIOutboxLock()
    {
        // Arrange + Act
        var (cache, _) = CreateCache();
        var sut = CreateLock(cache);

        // Then — 应用层锁必须满足 IOutboxLock 契约（Relay 编排代码零改动）
        sut.Should().BeAssignableTo<IOutboxLock>(
            "DistributedOutboxLock 是 IOutboxLock 的另一种实现；Relay 编排代码继续只依赖契约接口");
    }

    // ================================================================
    // Scenario 2: 首次获取 — 无人持锁时本实例必须拿到
    // ================================================================

    [Fact]
    public async Task GivenNoExistingLock_WhenTryAcquire_ThenReturnsTrue()
    {
        // Arrange — 全新缓存，空键
        var (cache, _) = CreateCache();
        IOutboxLock sut = CreateLock(cache, instanceId: "instance-A");

        // Act
        var acquired = await sut.TryAcquireAsync(
            messageId: "msg-1",
            instanceId: "instance-A",
            lease: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);

        // Then — 无人持锁 = 本实例必能拿到
        acquired.Should().BeTrue(
            "无人持锁时本实例 TryAcquire 必须返回 true — 这是 compare-and-set 风格的预期主路径");
    }

    // ================================================================
    // Scenario 3: 续约语义 — 同实例重复 TryAcquire 必须返回 true
    // ================================================================

    [Fact]
    public async Task GivenLockHeldBySelf_WhenTryAcquireAgain_ThenReturnsTrue()
    {
        // Arrange — 先让 instance-A 拿到
        var (cache, _) = CreateCache();
        IOutboxLock sut = CreateLock(cache, instanceId: "instance-A");
        await sut.TryAcquireAsync("msg-1", "instance-A", TimeSpan.FromSeconds(30), CancellationToken.None);

        // Act — 同实例再来一次
        var reacquired = await sut.TryAcquireAsync(
            messageId: "msg-1",
            instanceId: "instance-A",
            lease: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);

        // Then — 续约语义：本实例必须能续租
        reacquired.Should().BeTrue(
            "本实例已持锁的同一 messageId 再次 TryAcquire 必须返回 true — 续约语义是 Relay 编排的隐含依赖");
    }

    // ================================================================
    // Scenario 4: 互斥语义 — 他实例 TryAcquire 必须返回 false
    // ================================================================

    [Fact]
    public async Task GivenLockHeldByOtherInstance_WhenTryAcquireBySelf_ThenReturnsFalse()
    {
        // Arrange — instance-B 已持锁
        var (cache, _) = CreateCache();
        var sutB = CreateLock(cache, instanceId: "instance-B");
        IOutboxLock sutA = CreateLock(cache, instanceId: "instance-A");
        await sutB.TryAcquireAsync("msg-1", "instance-B", TimeSpan.FromSeconds(30), CancellationToken.None);

        // Act — instance-A 来抢
        var acquired = await sutA.TryAcquireAsync(
            messageId: "msg-1",
            instanceId: "instance-A",
            lease: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);

        // Then — 他人持锁 = 本实例必须跳过
        acquired.Should().BeFalse(
            "instance-B 已持锁时 instance-A TryAcquire 必须返回 false — 这是 #74 已知风险 #1 的核心防护");
    }

    // ================================================================
    // Scenario 5: Release — 本实例持有后 Release 必须能清掉
    // ================================================================

    [Fact]
    public async Task GivenLockHeldBySelf_WhenRelease_ThenLockHolds_NoOp()
    {
        // Arrange — instance-A 持锁
        var (cache, l2) = CreateCache();
        IOutboxLock sut = CreateLock(cache, instanceId: "instance-A");
        await sut.TryAcquireAsync("msg-1", "instance-A", TimeSpan.FromSeconds(30), CancellationToken.None);

        // Act — 本实例 Release（per-message 路径）
        await sut.ReleaseAsync("msg-1", "instance-A", CancellationToken.None);

        // Then — ReleaseAsync 是 no-op（PR #88 修）：lock key 仍在，由 lease 过期自然让出
        var key = "outbox:lock:msg-1";
        var remaining = l2.Get(key);
        remaining.Should().NotBeNull(
            "ReleaseAsync 是 no-op：lock key 必须仍存在（避免 race window 内其他 host 抢到同一锁 → exactly-once 失败）");

        // 二次 TryAcquire（同实例）必须仍为 owner
        var reAcquired = await sut.TryAcquireAsync("msg-1", "instance-A", TimeSpan.FromSeconds(30), CancellationToken.None);
        reAcquired.Should().BeTrue("同实例续约必须成功 — Release 不应影响 owner 身份");
    }

    // ================================================================
    // Scenario 6: Release — 所有权校验：他人持有时 Release 不能误删
    // ================================================================

    [Fact]
    public async Task GivenLockHeldByOtherInstance_WhenReleaseBySelf_ThenDoesNotClear()
    {
        // Arrange — instance-B 持锁
        var (cache, l2) = CreateCache();
        var sutB = CreateLock(cache, instanceId: "instance-B");
        IOutboxLock sutA = CreateLock(cache, instanceId: "instance-A");
        await sutB.TryAcquireAsync("msg-1", "instance-B", TimeSpan.FromSeconds(30), CancellationToken.None);

        // Act — instance-A 误调 Release
        Func<Task> act = () => sutA.ReleaseAsync("msg-1", "instance-A", CancellationToken.None);

        // Then — 不抛异常 + L2 仍由 B 持有
        await act.Should().NotThrowAsync(
            "Release 契约要求幂等；他人持有时调 Release 必须不抛异常");
        var key = "outbox:lock:msg-1";
        var remaining = await l2.GetAsync(key);
        remaining.Should().NotBeNull(
            "所有权校验：instance-A 不能误删 instance-B 的锁键，否则会出现锁被他人意外释放的灾难性 BUG");
        // L2 存的是 JSON 序列化的 owner string（IMultiLevelCache.GetOrSetAsync<string> 内部序列化），
        // 需要反序列化后再比对 — 见 DistributedOutboxLockTests 同款解释。
        System.Text.Json.JsonSerializer.Deserialize<string>(remaining!).Should().Be("instance-B",
            "Release 必须按 (messageId, instanceId) 校验所有权 — 仅当 value == 调用方 instanceId 时才清空");
    }

    // ================================================================
    // Scenario 7: Release — 锁不存在时幂等
    // ================================================================

    [Fact]
    public async Task GivenNoLockPresent_WhenRelease_ThenDoesNotThrow()
    {
        // Arrange — 全新 messageId
        var (cache, _) = CreateCache();
        IOutboxLock sut = CreateLock(cache, instanceId: "instance-A");

        // Act + Then — 不存在的锁 Release 必须幂等
        Func<Task> act = () => sut.ReleaseAsync("non-existent", "instance-A", CancellationToken.None);
        await act.Should().NotThrowAsync(
            "Release 必须幂等：键不存在时也不能抛异常，否则 Relay 编排会被意外中断");
    }

    // ================================================================
    // Scenario 8: key 命名空间 — 必须形如 outbox:lock:{messageId}
    // ================================================================

    [Fact]
    public async Task GivenAnyMessageId_WhenTryAcquire_ThenKeyFollowsOutboxLockNamespace()
    {
        // Arrange — 全新缓存
        var (cache, l2) = CreateCache();
        IOutboxLock sut = CreateLock(cache, instanceId: "instance-A");

        // Act
        await sut.TryAcquireAsync("msg-with-special-id_42", "instance-A", TimeSpan.FromSeconds(30), CancellationToken.None);

        // Then — 锁键必须落在 outbox:lock:{messageId} 命名空间下
        var expectedKey = "outbox:lock:msg-with-special-id_42";
        var actual = await l2.GetAsync(expectedKey);
        actual.Should().NotBeNull(
            "DistributedOutboxLock 必须使用约定的命名空间 'outbox:lock:{messageId}' — "
            + "便于运维按前缀清理 / 监控 / 排障");
        // L2 存的是 JSON 序列化的 owner string，需要反序列化（见 Release 测试同款解释）。
        System.Text.Json.JsonSerializer.Deserialize<string>(actual!).Should().Be("instance-A");
    }

    // ================================================================
    // Scenario 9: DI 选项 — OutboxLockProviderKind.Distributed 必须落本实现
    // ================================================================

    [Fact]
    public void GivenLockProviderDistributed_WhenResolvingIOutboxLock_ThenDistributedOutboxLockIsReturned()
    {
        // Arrange — 模拟 Distributed 模式 + L2 IDistributedCache 可用
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var l1 = new MemoryCache(new MemoryCacheOptions());
        var l2 = new InMemoryDistributedCache();
        services.AddSingleton<IMemoryCache>(l1);
        services.AddSingleton<IDistributedCache>(l2);
        services.AddSingleton<IMultiLevelCache, L1L2CacheForTest>();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(
            new OutboxRelayOptions
            {
                LockProvider = OutboxLockProviderKind.Distributed,
                LockInstanceId = "instance-A",
            }));
        // 通过 OutboxLockProvider 选择器（路径 A: 委托工厂 + 探测 IMultiLevelCache 是否就位）
        services.AddSingleton<IOutboxLock>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OutboxRelayOptions>>().Value;
            return opts.LockProvider == OutboxLockProviderKind.Distributed
                ? new DistributedOutboxLock(
                    sp.GetRequiredService<IMultiLevelCache>(),
                    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OutboxRelayOptions>>())
                : new NoOpOutboxLock();
        });

        // Act
        var sp = services.BuildServiceProvider();
        var lockObj = sp.GetRequiredService<IOutboxLock>();

        // Then
        lockObj.Should().BeOfType<DistributedOutboxLock>(
            "LockProvider=Distributed 必须解析为 DistributedOutboxLock — 这是 #82 应用层锁的核心入口");
    }

    // ================================================================
    // Scenario 10: LockProvider=Distributed 时 Relay 编排零改动
    //   这是契约层断言：IOutboxLock 抽象在 Distributed 模式下也保持完全一致
    // ================================================================

    [Fact]
    public async Task GivenDistributedLock_WhenRelayLoopRuns_ThenExactlyOneInstancePerMessageWins()
    {
        // Arrange — 模拟两个 Relay 实例（两个 DistributedOutboxLock 共用同一个 L2）
        var (cache, l2) = CreateCache();
        var sutA = CreateLock(cache, instanceId: "instance-A");
        var sutB = CreateLock(cache, instanceId: "instance-B");

        // Act — 两实例同时尝试拿同一把锁（A 先、B 后）
        var aWins = await sutA.TryAcquireAsync("msg-1", "instance-A", TimeSpan.FromSeconds(30), CancellationToken.None);
        var bWins = await sutB.TryAcquireAsync("msg-1", "instance-B", TimeSpan.FromSeconds(30), CancellationToken.None);

        // Then — 恰好一个赢
        aWins.Should().BeTrue("A 先到，无人持锁 → A 必赢");
        bWins.Should().BeFalse("B 后到，A 已持锁 → B 必须跳过（应用层锁的互斥语义）");

        // A Release（no-op，PR #88 修）后，B 仍抢不到（lock key 还在，lease 30s）
        await sutA.ReleaseAsync("msg-1", "instance-A", CancellationToken.None);
        var bRetry = await sutB.TryAcquireAsync("msg-1", "instance-B", TimeSpan.FromSeconds(30), CancellationToken.None);
        bRetry.Should().BeFalse(
            "A ReleaseAsync 是 no-op：lock key 仍在 lease 内 → B 必须仍抢不到（避免 PR #88 早期 race window 内 B 抢到锁 publish 第二次）");
    }
}
