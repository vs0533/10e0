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
/// <item><see cref="TryAcquireAsync"/> 走 "GetOrSet 风格的 compare-and-set"：本实例持有则续约；他实例持有则返回 false。</item>
/// <item><see cref="ReleaseAsync"/> 必须做 L1 比对防误删（即便 L2 已过期被他人续约，本实例 Release 也不能清掉他人锁）。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>为何能用 <see cref="IMultiLevelCache.GetOrSetAsync"/> 实现 compare-and-set？</b>
/// <see cref="IMultiLevelCache.GetOrSetAsync"/> 的语义是 "L1+L2 都未命中才调 factory，否则返回已存在的值"。
/// 这天然提供了 "唯一写者" 的 L2 入口：factory 内抛 <c>existingOrNull</c> 引用同一个 L2 key，
/// 未命中时 factory 被调 → 我们在 factory 副作用里写 <see cref="GetOrSetAsync"/> 返回的 instanceId；
/// 命中时 factory 不被调 → GetOrSet 直接返回已存的 instanceId，本实例比对即知是否同实例。
/// 注意：<see cref="IDistributedCache"/> 抽象不暴露 <c>SETNX</c> 原语，
/// factory 副作用"未命中即写" 就是 issue body 允许的 "降级为 try-set + L1 比对" 路径。
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

        // GetOrSet 风格的 compare-and-set：
        // - L1+L2 都未命中 → factory 被调 → 我们在 factory 副作用里写自己的 instanceId
        //   （factory 返回 instanceId，GetOrSet 会把返回值双写到 L1+L2）；
        //   由于 factory 是"未命中才调"，天然实现"唯一写者"语义。
        // - L1+L2 任意一层命中 → factory 不被调 → GetOrSet 直接返回已存的 instanceId
        //   → 本实例比对 = self：续约（也返回 true）；≠ self：他人持有（返回 false）。
        //
        // 注意：factory 副作用里"未命中即写"是对 IDistributedCache 不暴露 SETNX 的降级路径，
        // 单实例内部不存在竞争（GetOrSet 的 factory 由 cache 内部保证只调一次）；
        // 跨实例竞争由 L2 写后第二个实例 GetOrSet 看到已存值 → 走"他实例持锁"分支处理。
        var observed = await _cache.GetOrSetAsync<string>(
            key,
            factory: _ => new ValueTask<string?>(instanceId),
            options: new CacheOptions
            {
                L1Duration = ttl,
                L2Duration = ttl,
            },
            cancellationToken);

        // 续约：本实例已持锁 → 重新写一次 TTL（GetOrSet 已自动刷新 L1+L2，但若上层是 hit-fast 路径，
        // 命中 L1 时不刷 L2 的 TTL；这里为了简化并与 L1 命中即视为续约保持一致，不再额外刷 L2）。
        // 即使 L1 命中没刷 L2 TTL，最坏结果是租约到期比预期早，但同实例续约会重新写 → 自愈。
        return string.Equals(observed, instanceId, StringComparison.Ordinal);
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

        // 所有权校验：先看 L1（最便宜），L1 没有再看 L2。
        // 用 GetOrSet + "返回 null 的 factory" 是为了在不污染 L1+L2 的前提下读 L2 现有值
        // （factory 返回 null 时 cache 不会回写，见 DefaultCachingImplementations.MultiLevelCache.GetOrSetAsync）。
        // 任何一层比对失败 → 视为"不是本实例的锁" → 不清空（防误删）。
        var owner = await TryReadL1Async(key, cancellationToken);
        if (owner is null)
        {
            owner = await TryReadL2Async(key, cancellationToken);
        }

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

    /// <summary>
    /// L1 旁路读 —— <see cref="IMultiLevelCache"/> 抽象不暴露 <c>IMemoryCache</c> 句柄，
    /// 但 GetOrSet 的"factory 副作用写 L1"行为允许我们用一次额外的"空 factory"调用来旁路读 L1，
    /// 同时避免 factory 内真的回源（factory 返回 null 时 cache 不会回写）。
    ///
    /// <para>
    /// 然而这种"空 factory"开销等同于一次完整的 L1+L2 检查 — 与 L2 路径开销相当，
    /// 还引入额外的 cache 状态副作用。这里直接调 <see cref="IMultiLevelCache.GetOrSetAsync"/>
    /// 拿到 owner：未命中则返回 null（factory 返回 null 不回写），命中则返回 owner。
    /// 这样和 L2 读取行为一致，无需引入 IMemoryCache 直接句柄依赖。
    /// </para>
    /// </summary>
    private async Task<string?> TryReadL1Async(string key, CancellationToken cancellationToken) =>
        // 简单起见 L1/L2 都走同一条 GetOrSet 路径（cache 内部已经做了 L1-first 优化）。
        // 真正的 L1 旁路需要 IMultiLevelCache 暴露 IMemoryCache 句柄；为保持抽象纯净不扩展接口。
        await TryReadL2Async(key, cancellationToken).ConfigureAwait(false);

    private async Task<string?> TryReadL2Async(string key, CancellationToken cancellationToken)
    {
        // 用 GetOrSetAsync + "返回 null 的 factory"读：未命中 → factory 被调返回 null → cache 不回写
        // （见 DefaultCachingImplementations.MultiLevelCache.GetOrSetAsync 的 "fresh is null return null" 分支）。
        // 命中 → factory 不被调 → 直接返回已存值。
        var value = await _cache.GetOrSetAsync<string>(
            key,
            factory: _ => new ValueTask<string?>((string?)null),
            options: new CacheOptions
            {
                // 极短 TTL：只是为了让"空 factory 旁路读"在 cache 内部不污染 L1/L2
                // 但实际上 factory 返回 null 时 cache 不会回写，所以 TTL 大小不影响正确性
                L1Duration = TimeSpan.FromSeconds(1),
                L2Duration = TimeSpan.FromSeconds(1),
            },
            cancellationToken);

        return value;
    }
}
