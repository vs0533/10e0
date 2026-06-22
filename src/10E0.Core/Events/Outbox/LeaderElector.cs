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
/// <item>Leader 选举通过 <see cref="IMultiLevelCache"/> L2 持久化（<c>{prefix}:election</c> 命名空间），
///   value 为当前 leader 的 <c>instanceId</c>。</item>
/// <item>抢主语义用 <see cref="IMultiLevelCache.TrySetAsync"/>（NX）：仅当 L2 不存在时新 instance 可写入；
///   <see cref="ReleaseAsync"/> 仅当 L2 owner == self 时清空（退位）。</item>
/// <item>续约：同实例再次 <see cref="TryAcquireAsync"/> 时 <see cref="IMultiLevelCache.SetAsync"/> 覆盖写
///   刷新 L2 TTL；永远不会把自己误失效。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>已知简化</b>：L1 cache 可能让短 TTL 内的过期判定出现微小窗口；
/// L2 是真相源（每次 Release 都双清）。生产部署请确保 L2 是共享 Redis 而非进程内模拟。
/// </para>
///
/// <para>
/// <b>不验证</b>：跨进程 Redis 时钟漂移、Redis 集群脑裂等运维问题。
/// </para>
/// </summary>
public sealed class LeaderElector : IOutboxLock
{
    private readonly IMultiLevelCache _cache;
    private readonly OutboxRelayOptions _options;

    /// <summary>
    /// 构造 Leader Election 模式的锁 provider。
    /// </summary>
    /// <param name="cache">L1+L2 多级缓存（leader key 的真值源）。</param>
    /// <param name="options">从 <see cref="IOptions{TOptions}"/> 注入 — 读 <c>LockInstanceId</c> / <c>LeaderLeaseDuration</c>。</param>
    public LeaderElector(IMultiLevelCache cache, IOptions<OutboxRelayOptions> options)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
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
        var ttl = lease > TimeSpan.Zero ? lease : _options.LeaderLeaseDuration;

        // 第一次抢主：TrySetAsync 真 SETNX — key 不存在时写入自己 instanceId，返回 true
        var acquired = await _cache.TrySetAsync<string>(
            leaderKey,
            instanceId,
            BuildCacheOptions(ttl),
            cancellationToken).ConfigureAwait(false);

        if (acquired)
        {
            return true;
        }

        // 已有 leader：读 owner 判断是否自己（同实例续约）
        var current = await _cache.GetOrSetAsync<string>(
            leaderKey,
            _ => ValueTask.FromResult<string?>(null),
            BuildCacheOptions(ttl),
            cancellationToken).ConfigureAwait(false);

        if (!string.Equals(current, instanceId, StringComparison.Ordinal))
        {
            // 他人 leader
            return false;
        }

        // 续约：覆盖写 + 刷新 TTL
        await _cache.SetAsync<string>(
            leaderKey,
            instanceId,
            BuildCacheOptions(ttl),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(
        string messageId,
        string instanceId,
        CancellationToken cancellationToken)
    {
        _ = messageId;

        // Leader 模式 Release 语义：no-op —— Leader 是全局身份，不应因"处理完一条消息"就退位。
        // 早期实现调 RemoveAsync 删 leader key，触发 PR #88 docker-integration-tests CI 暴露的真 bug：
        //   - hostA 处理完 msg-000 后 Release 删 leader key
        //   - hostB 立即抢主成功 → publish msg-000 → Release 又删 leader key
        //   - 两条 host 轮流 publish 同一 message 2 次 → exactly-once 失败
        // 正确语义：Leader 身份由 lease 过期自然让出（<see cref="TryAcquireAsync"/> 续约刷新 TTL，
        // 不续约则 lease 过期后其他实例能抢主）。实例 shutdown 时由调用方显式调专门的退位 API
        // （尚未提供——见后续 issue）。
        await Task.CompletedTask;
        _ = cancellationToken;
        _ = instanceId;
    }

    private static string BuildLeaderKey(string prefix) => $"{prefix}:election";

    private static CacheOptions BuildCacheOptions(TimeSpan ttl) => new()
    {
        L1Duration = ttl,
        L2Duration = ttl,
    };
}
