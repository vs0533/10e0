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
        _ = messageId;
        _ = instanceId;
        _ = cancellationToken;

        // Distributed 模式 Release 语义：no-op —— lock key 应由 lease 过期自然让出。
        //
        // 早期实现（PR #88 早期）调 RemoveAsync 删 lock key，docker-integration-tests CI 暴露的真 bug：
        //   - hostA 处理 msg-000：TryAcquireAsync ✓ → publish（mock 收到）→ SaveChangesAsync 还没 commit →
        //     ReleaseAsync 删 lock key
        //   - hostB 同时 pick 同一 msg-000（SentTime 还是 null）：TryAcquireAsync ✓（lock 不在了）→
        //     publish 第二次 → exactly-once 失败
        //
        // 正确语义：lease = owner 的"持续声明"窗口，lock key 在 lease 内（默认 30s）由 owner 独占。
        // lease 过期自动让出（无需显式删），期间其他 host 抢锁必失败。消息本身由 SQL 的 SentTime
        // 标记为已发布，pick-up SQL (`SentTime == null`) 自然过滤。lock key 是临时"占位"，30s 后
        // 自然消失，无需立即删除。
        //
        // 副作用：lock key 会留存到 lease 过期。生产部署 messageId 是 outbox:lock:{messageId}，
        // 单实例每条消息一个 key × lease 30s × 投递速率 = 内存可控；分布式 Redis 由 LRU/TTL 自动回收。
        await Task.CompletedTask;
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
