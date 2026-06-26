using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenE0.Core.Observability;

namespace TenE0.Core.Events.Outbox;

/// <summary>Outbox Relay 的运行参数。</summary>
public sealed class OutboxRelayOptions
{
    /// <summary>每次轮询取出的最大消息数。</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>两次轮询之间的间隔。空闲时建议长一些，繁忙时短一些。</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>单条消息的最大重试次数（达到后保留在表中并记录错误，不再尝试）。</summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>
    /// 锁租约时长（TryAcquire 时写入 <c>LockedUntil</c> 的偏移量）。
    /// 默认 30s：远长于单次 publish 调用的预期耗时（通常毫秒级），
    /// 但短到一旦实例崩溃，另一实例能在可接受时间内接管（&lt; 1 分钟）。
    /// </summary>
    public TimeSpan LockLeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 当前实例的唯一标识；同时作为 <c>LockedByInstance</c> 写入行。
    /// 默认 <c>Environment.MachineName + Guid.NewGuid()</c>：
    /// 同机多实例（容器/端口隔离场景）天然不冲突；跨机部署天然不冲突。
    /// 若需更强隔离（例如蓝绿部署需保证不同批次不互踩），可通过配置显式覆盖。
    /// </summary>
    public string LockInstanceId { get; set; } =
        $"{Environment.MachineName}-{Guid.NewGuid():N}";

    /// <summary>
    /// 行级锁 provider 选择：决定 <c>IOutboxLock</c> 实际注入哪种实现。
    /// 默认 <see cref="OutboxLockProviderKind.None"/> — 与 <see cref="NoOpOutboxLock"/> 等价，
    /// 让 0/1 实例部署零感知。配置为 <see cref="OutboxLockProviderKind.RowLock"/> 后，
    /// DI 层会按底层 EF Core ProviderName（SqlServer / PostgreSQL）命名匹配选择具体实现。
    /// <see cref="OutboxLockProviderKind.Distributed"/> 留给后续 Redis 等分布式锁场景。
    /// <see cref="OutboxLockProviderKind.Leader"/> 启用 Leader Election 模式 — 全局只一个 Relay 实例承担投递，
    /// 从根上消除竞争（详见 feature #82 LeaderElection）。
    /// </summary>
    public OutboxLockProviderKind LockProvider { get; set; } = OutboxLockProviderKind.None;

    /// <summary>
    /// Leader Election 租约时长（<see cref="LockProvider"/> = <see cref="OutboxLockProviderKind.Leader"/> 时生效）：
    /// LeaderElector 抢主成功后写入分布式存储的 lease 过期时间。Lease 过期后另一实例可抢主。
    /// 默认 30s：远长于单次 publish 调用的预期耗时（通常毫秒级），但短到一旦 Leader 实例崩溃，
    /// 另一实例能在可接受时间内接管（&lt; 1 分钟）。
    /// </summary>
    public TimeSpan LeaderLeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Leader Election 写入分布式存储（如 Redis）时使用的 key 前缀。
    /// 默认 <c>"outbox:leader"</c>：让多套环境（dev/staging/prod）共用同一 Redis 时不冲突。
    /// 若需更强隔离（例如蓝绿部署需保证不同批次不互踩），可通过配置显式覆盖。
    /// </summary>
    public string LeaderInstanceKeyPrefix { get; set; } = "outbox:leader";
}

/// <summary>
/// Outbox 中继服务 — 把 OutboxMessage 表中未发送的事件持续投递出去。
///
/// 职责拆分：
/// - 本类只负责 "拉取批次 → 调投递器 → 更新状态 → 重试"
/// - 实际投递逻辑由 <see cref="IOutboxPublisher"/> 决定（进程内 / Kafka / CAP / RabbitMQ）
/// - 切换投递机制时，本类零改动
///
/// 失败处理：
/// - 抛异常即视为本次投递失败，AttemptCount + 1，LastError 记录原因
/// - 超过 MaxAttempts 后停止尝试（视为毒消息，需人工介入或 DLQ 工具处理）
/// - 单条消息失败不影响同批次其他消息（独立 try/catch）
/// </summary>
public sealed class OutboxRelayService<TContext>(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxRelayOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxRelayService<TContext>> logger) : BackgroundService
    where TContext : DbContext
{
    private readonly OutboxRelayOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("OutboxRelayService 启动：BatchSize={Batch}, Interval={Interval}",
            _options.BatchSize, _options.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken);
                if (processed == 0)
                    await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "OutboxRelayService 轮询发生异常，{Delay} 后重试", _options.PollInterval);
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
        }
    }

    /// <summary>
    /// 处理一批未发布消息：pick 候选 → 抢锁 → publish → 释放 → SaveChanges。
    /// 标记为 <c>internal</c>（不再 <c>private</c>）让测试项目（<c>10E0.Core.Tests</c>）可直接调
    /// 而无需走反射 —— 反射调用对 private 签名变更脆弱，internal + InternalsVisibleTo 更稳。
    /// </summary>
    internal async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var dcFactory = sp.GetRequiredService<IDbContextFactory<TContext>>();
        var publisher = sp.GetRequiredService<IOutboxPublisher>();
        var outboxLock = sp.GetRequiredService<IOutboxLock>();
        // #161 可观测性埋点：未注册 Observability 时 metrics == null → no-op。
        var metrics = sp.GetService<TenE0Metrics>();

        await using var dc = await dcFactory.CreateDbContextAsync(cancellationToken);
        var batch = await dc.Set<OutboxMessage>()
            .Where(m => m.SentTime == null && m.AttemptCount < _options.MaxAttempts)
            .OrderBy(m => m.OccurredOn)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        if (batch.Count == 0)
            return 0;

        foreach (var msg in batch)
        {
            // 先尝试获取行级锁（feature #82 集成）。
            // 契约（见 IOutboxLock）：返回 false 时本轮跳过本条消息，
            // 不应 ++AttemptCount — 由真正持有锁的实例处理，租约过期后另一实例接管。
            // Leader 模式下，非 leader 整轮全返回 false（全局一把锁）— 与"只让 leader 跑 Relay 全流程"语义一致。
            var acquired = await outboxLock.TryAcquireAsync(
                msg.Id,
                _options.LockInstanceId,
                _options.LockLeaseDuration,
                cancellationToken).ConfigureAwait(false);
            if (!acquired)
            {
                logger.LogDebug(
                    "Outbox 消息行级锁未获取，跳过 Id={Id} Instance={Instance}",
                    msg.Id, _options.LockInstanceId);
                continue;
            }

            msg.AttemptCount++;
            try
            {
                await publisher.PublishAsync(msg, cancellationToken);
                msg.SentTime = timeProvider.GetUtcNow();
                msg.LastError = null;
                metrics?.OutboxDelivered.Add(1,
                [
                    new(TenE0Metrics.Tags.Result, TenE0Metrics.Tags.Success),
                ]);
            }
            catch (Exception ex)
            {
                msg.LastError = Truncate(ex.Message, 2000);
                logger.LogWarning(ex,
                    "Outbox 消息投递失败 Id={Id} Attempt={Attempt}/{Max}",
                    msg.Id, msg.AttemptCount, _options.MaxAttempts);
                metrics?.OutboxDelivered.Add(1,
                [
                    new(TenE0Metrics.Tags.Result, TenE0Metrics.Tags.Failure),
                ]);
            }
            finally
            {
                // 异常路径也必须释放 — 实现层校验 (msg.Id, instanceId) 后清空，
                // 不属于本实例的锁由实现层拒绝，绝不误删他实例锁。
                await outboxLock.ReleaseAsync(
                    msg.Id,
                    _options.LockInstanceId,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await dc.SaveChangesAsync(cancellationToken);

        // #161 刷新积压指标：仅启用 Observability 时跑一次 CountAsync（每轮一次，可接受）。
        // 口径与 OutboxHealthCheck 及候选查询一致：SentTime == null && AttemptCount < MaxAttempts
        // （排除已超 MaxAttempts 的毒消息 —— 那些是 DLQ 运维问题，不应让积压计数虚高，否则
        // Prometheus 指标与 /health 报告对不齐，误导运维告警阈值）。
        if (metrics is not null)
        {
            var backlog = await dc.Set<OutboxMessage>()
                .CountAsync(m => m.SentTime == null && m.AttemptCount < _options.MaxAttempts, cancellationToken);
            metrics.SetBacklog(backlog);
        }

        return batch.Count;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max];
}
