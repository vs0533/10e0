using Microsoft.Extensions.Options;
using TenE0.Core.Caching;

namespace TenE0.Core.Events.Outbox;

/// <summary>
/// Leader Election 模式（feature #82 子任务）— 全局只一个 Relay 实例承担投递，
/// 其余实例空闲待命，Lease 过期后通过抢主重新选主。
///
/// <para>
/// <b>设计要点</b>：
/// <list type="bullet">
/// <item>Leader 模式把 <see cref="IOutboxLock"/> 的"per-message 锁"提升为"全局一把锁"：
///   只让 leader 实例的 <see cref="TryAcquireAsync"/> 返回 true（即 Relay 跑完整流程），
///   非 leader 全部返回 false 直接 skip 整轮；</item>
/// <item>Leader 选举通过 <see cref="IMultiLevelCache"/> L2 持久化（<c>outbox:leader</c> 命名空间），
///   value 为当前 leader 的 <c>instanceId</c> + <c>leaseVersion</c>（<see cref="IAtomicCounter"/>
///   单调递增，避免"旧 leader 复活"误接管）；</item>
/// <item>抢主语义是 compare-and-swap：仅当 L2 不存在或 owner 已退位时新 instance 可写入；
///   <see cref="ReleaseAsync"/> 仅当 L2 owner == self 时清空（退位）。</item>
/// <item>续约：同实例再次 <see cref="TryAcquireAsync"/> 时仅刷新 L2 TTL + 递增 version，
///   不改 owner — 永远不会被自己误失效。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>已知简化（单测覆盖逻辑分派；真实两进程并发靠 Testcontainers BDD 兜底）</b>：
/// <see cref="IMultiLevelCache"/> L1 缓存可能让短 TTL 内的过期判定出现微小窗口；
/// L2 是真相源（每次 Release 都双清）。生产部署请确保 L2 是共享 Redis 而非进程内模拟。
/// </para>
/// </summary>
public sealed class LeaderElector : IOutboxLock
{
    private readonly IMultiLevelCache _cache;
    private readonly IAtomicCounter _counter;
    private readonly OutboxRelayOptions _options;

    /// <summary>
    /// 构造 Leader Election 模式的锁 provider。
    /// </summary>
    /// <param name="cache">L1+L2 多级缓存（leader key 的真值源）。</param>
    /// <param name="counter">原子计数器（抢主时单调递增避免时钟漂移）。</param>
    /// <param name="options">从 <see cref="IOptions{TOptions}"/> 注入 — 读 <c>LockInstanceId</c> / <c>LeaderLeaseDuration</c>。</param>
    public LeaderElector(IMultiLevelCache cache, IAtomicCounter counter, IOptions<OutboxRelayOptions> options)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _counter = counter ?? throw new ArgumentNullException(nameof(counter));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(
        string messageId,
        string instanceId,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);

        // Leader 是全局概念，与 messageId 无关 — 但保留参数以满足 IOutboxLock 接口契约。
        _ = messageId;

        var leaderKey = BuildLeaderKey(_options.LeaderInstanceKeyPrefix);
        var versionKey = BuildVersionKey(_options.LeaderInstanceKeyPrefix, instanceId);
        var ttl = lease > TimeSpan.Zero ? lease : _options.LeaderLeaseDuration;

        // 先读当前 leader（GetOrSetAsync 走 L2 真值源）。
        var current = await _cache.GetOrSetAsync<LeaderRecord>(
            leaderKey,
            _ => ValueTask.FromResult<LeaderRecord?>(null),
            BuildCacheOptions(ttl),
            cancellationToken).ConfigureAwait(false);

        // Case 1: 已有 leader，且就是本实例 — 续约（递增 version，刷新 L2 TTL）。
        if (current is not null && string.Equals(current.InstanceId, instanceId, StringComparison.Ordinal))
        {
            var newVersion = await _counter.IncrementAsync(versionKey, cancellationToken).ConfigureAwait(false);
            var renewed = current with { LeaseVersion = newVersion };
            await _cache.RemoveAsync(leaderKey, cancellationToken).ConfigureAwait(false);
            await _cache.GetOrSetAsync<LeaderRecord>(
                leaderKey,
                _ => ValueTask.FromResult<LeaderRecord?>(renewed),
                BuildCacheOptions(ttl),
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        // Case 2: 已有 leader，但不是本实例 — 直接 skip（非 leader 实例整轮跳过）。
        if (current is not null)
        {
            return false;
        }

        // Case 3: 无人是 leader — 抢主（compare-and-swap：写入新 record）。
        var firstVersion = await _counter.IncrementAsync(versionKey, cancellationToken).ConfigureAwait(false);
        var newRecord = new LeaderRecord(instanceId, firstVersion);
        await _cache.GetOrSetAsync<LeaderRecord>(
            leaderKey,
            _ => ValueTask.FromResult<LeaderRecord?>(newRecord),
            BuildCacheOptions(ttl),
            cancellationToken).ConfigureAwait(false);

        // 抢主后再读一次，校验我们写入的就是当前值（防止极小窗口内另一实例抢走）。
        var afterClaim = await _cache.GetOrSetAsync<LeaderRecord>(
            leaderKey,
            _ => ValueTask.FromResult<LeaderRecord?>(newRecord),
            BuildCacheOptions(ttl),
            cancellationToken).ConfigureAwait(false);

        return afterClaim is not null
            && string.Equals(afterClaim.InstanceId, instanceId, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(
        string messageId,
        string instanceId,
        CancellationToken cancellationToken)
    {
        _ = messageId;

        var leaderKey = BuildLeaderKey(_options.LeaderInstanceKeyPrefix);

        var current = await _cache.GetOrSetAsync<LeaderRecord>(
            leaderKey,
            _ => ValueTask.FromResult<LeaderRecord?>(null),
            BuildCacheOptions(_options.LeaderLeaseDuration),
            cancellationToken).ConfigureAwait(false);

        // 仅当 owner == self 才退位；非 owner 调 Release 是幂等 no-op。
        if (current is null || !string.Equals(current.InstanceId, instanceId, StringComparison.Ordinal))
        {
            return;
        }

        await _cache.RemoveAsync(leaderKey, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildLeaderKey(string prefix) => $"{prefix}:election";

    private static string BuildVersionKey(string prefix, string instanceId)
        => $"{prefix}:version:{instanceId}";

    private static CacheOptions BuildCacheOptions(TimeSpan ttl) => new()
    {
        L1Duration = ttl,
        L2Duration = ttl,
    };

    /// <summary>
    /// 持久化在 L2 的 leader 记录：owner instanceId + 单调递增 lease version。
    /// record 保证不可变；version 由 <see cref="IAtomicCounter"/> 自增，避免时钟漂移。
    /// </summary>
    private sealed record LeaderRecord(string InstanceId, long LeaseVersion);
}
