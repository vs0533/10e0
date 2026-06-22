using Microsoft.Extensions.Options;
using TenE0.Core.Caching;

namespace TenE0.Core.Events.Outbox;

/// <summary>
/// 基于 <see cref="IMultiLevelCache"/> L2（<c>IDistributedCache</c>）的应用层分布式锁 provider ——
/// feature #82 子任务：多实例 Relay 在无数据库行级锁环境下也能通过 L2 互斥抢占同一 OutboxMessage。
///
/// <para>
/// <b>实现策略</b>：
/// <list type="bullet">
/// <item>key 命名空间 = <c>outbox:lock:{messageId}</c>；value = <c>instanceId</c> 字符串。</item>
/// <item><see cref="TryAcquireAsync"/> 走 <see cref="IMultiLevelCache.TrySetAsync"/> 真 SETNX 抢锁；
///   抢到 → true；已有 owner 且是自己 → <see cref="IMultiLevelCache.SetAsync"/> 续约；已有 owner 是他人 → false。</item>
/// <item><see cref="ReleaseAsync"/> 必须做 L1 比对防误删（即便 L2 已过期被他人续约，本实例 Release 也不能清掉他人锁）。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>原子性</b>：<see cref="IMultiLevelCache.TrySetAsync"/> 在生产 Redis 后端实现下用 <c>SET key NX EX</c>
/// 保证单调用原子；本抽象在单进程 + MemoryDistributedCache 测试场景下用 L1 TryGetValue 串行化保证近似原子。
/// </para>
///
/// <para>
/// <b>租约来源</b>：从 <see cref="OutboxRelayOptions.LockLeaseDuration"/> 读取。
/// 调用方传入的 <c>lease</c> 参数仍以 <c>OutboxRelayOptions.LockLeaseDuration</c> 为准（保持与
/// <see cref="SqlServerOutboxLock{T}"/> / <see cref="PostgresOutboxLock{T}"/> 的"权威来源"语义一致）。
/// </para>
///
/// <para>
/// <b>不验证</b>：跨进程 Redis 时钟漂移、Redis 集群脑裂等运维问题（属于 issue 后续运维验证范畴）。
/// </para>
/// </summary>
public sealed class DistributedOutboxLock : IOutboxLock
{
    private readonly IMultiLevelCache _cache;
    private readonly OutboxRelayOptions _options;

    /// <summary>
    /// 构造基于 <see cref="IMultiLevelCache"/> 的应用层锁 provider。
    /// </summary>
    /// <param name="cache">L1 (进程内) + L2 (分布式) 多级缓存抽象。L2 才是互斥真相源；L1 仅做热路径优化。</param>
    /// <param name="options">从 <see cref="IOptions{TOptions}"/> 注入 — 读取 <c>LockInstanceId</c> / <c>LockLeaseDuration</c>。</param>
    public DistributedOutboxLock(IMultiLevelCache cache, IOptions<OutboxRelayOptions> options)
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
        if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(instanceId))
        {
            return false;
        }

        var key = BuildKey(messageId);
        var ttl = ResolveLease(lease);

        // 第一次抢锁：TrySetAsync 真 SETNX — key 不存在时写入自己 instanceId，返回 true
        var acquired = await _cache.TrySetAsync<string>(
            key,
            instanceId,
            new CacheOptions { L1Duration = ttl, L2Duration = ttl },
            cancellationToken);

        if (acquired)
        {
            return true;
        }

        // key 已存在：读 owner 判断是否自己（同实例续约）
        var observed = await _cache.GetOrSetAsync<string>(
            key,
            factory: _ => new ValueTask<string?>(instanceId),
            new CacheOptions { L1Duration = ttl, L2Duration = ttl },
            cancellationToken);

        if (!string.Equals(observed, instanceId, StringComparison.Ordinal))
        {
            // 他人持有
            return false;
        }

        // 续约：覆盖写 + 刷新 TTL
        await _cache.SetAsync<string>(
            key,
            instanceId,
            new CacheOptions { L1Duration = ttl, L2Duration = ttl },
            cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(
        string messageId,
        string instanceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        var key = BuildKey(messageId);

        // 所有权校验：用 GetOrSet + 返回 null 的 factory 旁路读 L2
        // （factory 返回 null 时 cache 不会回写 → 见 DefaultCachingImplementations.MultiLevelCache.GetOrSetAsync）
        var owner = await _cache.GetOrSetAsync<string>(
            key,
            factory: _ => new ValueTask<string?>((string?)null),
            new CacheOptions
            {
                // 极短 TTL；实际上 factory 返回 null 时 cache 不回写，所以 TTL 大小不影响正确性
                L1Duration = TimeSpan.FromSeconds(1),
                L2Duration = TimeSpan.FromSeconds(1),
            },
            cancellationToken);

        if (!string.Equals(owner, instanceId, StringComparison.Ordinal))
        {
            // 不持有 或 已被他人续约 → 幂等返回，不抛异常
            return;
        }

        // 真删：双层都清掉
        await _cache.RemoveAsync(key, cancellationToken);
    }

    private static string BuildKey(string messageId) => $"outbox:lock:{messageId}";

    private TimeSpan ResolveLease(TimeSpan requested)
    {
        // 调用方传入 lease 与配置项不一致时，以配置项为权威来源（与 SqlServerOutboxLock / PostgresOutboxLock 保持一致）
        if (_options.LockLeaseDuration > TimeSpan.Zero)
        {
            return _options.LockLeaseDuration;
        }
        return requested > TimeSpan.Zero ? requested : TimeSpan.FromSeconds(30);
    }
}
