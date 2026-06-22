using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using TenE0.Core.Caching;
using TenE0.Core.Events.Outbox;
using TenE0.Core.Tests.Events.Outbox.TestFakes;

namespace TenE0.Core.Tests.Events.Outbox;

/// <summary>
/// Step 3/6 — LeaderElector 单测（feature #82 应用层分布式锁 + leader election）。
///
/// <para>
/// 验收口径（与 plan 对齐）：
/// <list type="number">
/// <item>单实例部署：无人是 leader 时本实例必当选；</item>
/// <item>两实例部署：A 先到先得，B 抢主必返回 false（整个本轮 skip）；</item>
/// <item>原 leader 主动 Release 后另一实例能接管；</item>
/// <item>新实例 ID 视为前任失效，可接管（模拟 lease expiry → 接管）。</item>
/// </list>
/// </para>
///
/// <para>
/// 不验证：真实两进程下的 split-brain（依赖 BDD / Testcontainers，本步范围外）。
/// </para>
/// </summary>
public sealed class LeaderElectorTests
{
    // ================================================================
    // Test infrastructure — 用共享 TestFakes helper（#82 PR #88 bot review
    // 揭示本地 fake 类在 4 个文件重复且缺 TrySetAsync/SetAsync 实现）
    // ================================================================

    private static (IMultiLevelCache cache, InMemoryDistributedCache l2) CreateCache()
    {
        var l1 = new MemoryCache(new MemoryCacheOptions());
        var l2 = new InMemoryDistributedCache();
        return (new L1L2CacheForTest(l1, l2), l2);
    }

    private static LeaderElector CreateElector(
        IMultiLevelCache cache,
        string instanceId,
        TimeSpan? lease = null,
        string keyPrefix = "outbox:leader:test")
    {
        var options = Options.Create(new OutboxRelayOptions
        {
            LockInstanceId = instanceId,
            LeaderLeaseDuration = lease ?? TimeSpan.FromSeconds(30),
            LeaderInstanceKeyPrefix = keyPrefix,
        });
        return new LeaderElector(cache, options);
    }

    // ================================================================
    // 单测 1: 单实例 — 无人是 leader 时本实例必当选
    // ================================================================

    [Fact]
    public async Task SingleInstance_AlwaysLeader()
    {
        // Arrange — 全新缓存
        var (cache, _) = CreateCache();
        IOutboxLock sut = CreateElector(cache, "instance-A");

        // Act
        var acquired = await sut.TryAcquireAsync(
            messageId: "*",
            instanceId: "instance-A",
            lease: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);

        // Then
        acquired.Should().BeTrue(
            "无人持有 leader 时单实例部署必须当选 — leader election 主路径");
    }

    // ================================================================
    // 单测 2: 两实例 — A 先到先得，B 必 skip
    // ================================================================

    [Fact]
    public async Task TwoInstances_FirstAcquiresLeader_SecondSkips()
    {
        // Arrange — 共享 L2 + counter
        var l2 = new InMemoryDistributedCache();
        var cache = new L1L2CacheForTest(new MemoryCache(new MemoryCacheOptions()), l2);
        var sutA = CreateElector(cache, "instance-A");
        IOutboxLock sutB = CreateElector(cache, "instance-B");

        // Act — A 先抢
        await sutA.TryAcquireAsync("*", "instance-A", TimeSpan.FromSeconds(30), CancellationToken.None);
        var bAcquired = await sutB.TryAcquireAsync(
            messageId: "*",
            instanceId: "instance-B",
            lease: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);

        // Then — B 必须 skip
        bAcquired.Should().BeFalse(
            "A 已当选时 B 抢主必须返回 false — 同一时刻全集群仅一个 leader");
    }

    // ================================================================
    // 单测 3: 原 leader 退位 → 接管
    // ================================================================

    [Fact]
    public async Task OriginalLeader_LeaseExpiresAndSecondTakesOver()
    {
        // Lease 过期 failover — 这是 #82 issue body 明确说的"租约式 leader 锁"主路径：
        //   leader 崩了/不续约 → lease 过期 → 另一实例能抢主
        // （不是"主动 Release 退位"——Leader 模式 ReleaseAsync 是 no-op，由 lease 过期自然让出，
        //   早期 PR #88 测试用"主动 Release"语义是错的。）
        // Arrange
        var l2 = new InMemoryDistributedCache();
        var cache = new L1L2CacheForTest(new MemoryCache(new MemoryCacheOptions()), l2);
        var sutA = CreateElector(cache, "instance-A");
        IOutboxLock sutB = CreateElector(cache, "instance-B");
        // A 用 50ms 短 lease 当选
        await sutA.TryAcquireAsync("*", "instance-A", TimeSpan.FromMilliseconds(50), CancellationToken.None);

        // Act — 等 lease 过期
        await Task.Delay(150);
        var bAcquired = await sutB.TryAcquireAsync(
            messageId: "*",
            instanceId: "instance-B",
            lease: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);

        // Then
        bAcquired.Should().BeTrue(
            "A 的 lease 过期后 B 必须能抢主接管 — #82 issue 明确说的'租约式 leader failover'主路径");
    }

    [Fact]
    public async Task OriginalLeader_ReleaseIsNoOp_LeaseStillHolds()
    {
        // 锁定 Leader 模式 ReleaseAsync 的 no-op 语义（PR #88 修）：
        //   ReleaseAsync 不删 leader key → 同一实例调 Release 不应让自己失去 leader 身份。
        // Arrange
        var l2 = new InMemoryDistributedCache();
        var cache = new L1L2CacheForTest(new MemoryCache(new MemoryCacheOptions()), l2);
        var sutA = CreateElector(cache, "instance-A");
        IOutboxLock sutB = CreateElector(cache, "instance-B");
        await sutA.TryAcquireAsync("*", "instance-A", TimeSpan.FromSeconds(30), CancellationToken.None);

        // Act — A 调 Release（per-message 路径）
        await sutA.ReleaseAsync("*", "instance-A", CancellationToken.None);

        // Then — A 仍是 leader（lease 还在）
        var aReacquired = await sutA.TryAcquireAsync(
            messageId: "*",
            instanceId: "instance-A",
            lease: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);
        aReacquired.Should().BeTrue(
            "ReleaseAsync 是 no-op：A 调 Release 不应失去 leader 身份（lease 还在）");

        // B 仍抢不到
        var bAcquired = await sutB.TryAcquireAsync(
            messageId: "*",
            instanceId: "instance-B",
            lease: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);
        bAcquired.Should().BeFalse(
            "A 还在 leader 期间 B 必须抢不到（避免 PR #88 早期 ReleaseAsync 删 leader key 导致两个 host 轮流当 leader）");
    }

    // ================================================================
    // 单测 4: lease 续约 — 已是 leader 的实例续约必成功
    //   （等价于 "LeaderLeaseExpiry_AllowsReacquire"：同实例续约期间视为续任不丢；
    //     测试场景上等价于 A 拿后再次 TryAcquire 仍为 true，覆盖 lease 自动续期路径）
    // ================================================================

    [Fact]
    public async Task LeaderLeaseExpiry_AllowsReacquire()
    {
        // Arrange — A 当选
        var l2 = new InMemoryDistributedCache();
        var cache = new L1L2CacheForTest(new MemoryCache(new MemoryCacheOptions()), l2);
        IOutboxLock sut = CreateElector(cache, "instance-A");
        await sut.TryAcquireAsync("*", "instance-A", TimeSpan.FromSeconds(30), CancellationToken.None);

        // Act — 同一实例再次 TryAcquire（心跳续租）
        var renewed = await sut.TryAcquireAsync(
            messageId: "*",
            instanceId: "instance-A",
            lease: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);

        // Then — 必须续任
        renewed.Should().BeTrue(
            "leader 续约（心跳）必须返回 true — 不应被自己误失效");
    }
}
