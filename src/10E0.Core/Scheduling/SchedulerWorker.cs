using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenE0.Core.Observability;
using TenE0.Core.Scheduling.Entities;

namespace TenE0.Core.Scheduling;

/// <summary>
/// 定时任务调度后台 Worker（issue #164）—— 周期性扫描到期任务、抢锁、交执行器执行。
///
/// <para>
/// 模式对齐 <c>OutboxRelayService&lt;TContext&gt;</c>：
/// <list type="bullet">
/// <item><see cref="ExecuteAsync"/> 死循环 + <c>Task.Delay(ScanInterval)</c> 退避。</item>
/// <item>每轮 pick 到期任务（<c>IsEnabled AND NextRunAt &lt;= now</c>）→ 抢锁 → 执行 → 释放。</item>
/// <item>内部 <see cref="ProcessBatchAsync"/> 标记 internal，供测试直接调（<c>InternalsVisibleTo</c>）。</item>
/// </list>
/// </para>
/// </summary>
/// <typeparam name="TContext">承载任务表的 EF Core DbContext 类型。</typeparam>
public sealed class SchedulerWorker<TContext>(
    IServiceScopeFactory scopeFactory,
    IOptions<SchedulingOptions> options,
    TimeProvider timeProvider,
    ILogger<SchedulerWorker<TContext>> logger) : BackgroundService
    where TContext : DbContext
{
    private readonly SchedulingOptions _options = options.Value;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SchedulerWorker 启动：ScanInterval={Interval} LockProvider={Lock}",
            _options.ScanInterval, _options.LockProvider);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken);
                if (processed == 0)
                {
                    await Task.Delay(_options.ScanInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "SchedulerWorker 轮询发生异常，{Delay} 后重试", _options.ScanInterval);
                await Task.Delay(_options.ScanInterval, stoppingToken);
            }
        }
    }

    /// <summary>
    /// 处理一批到期任务：pick → 抢锁 → 执行 → 释放。
    /// 标记 internal 让测试项目直接调，无需走 BackgroundService 生命周期。
    /// </summary>
    internal async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var dcFactory = sp.GetRequiredService<IDbContextFactory<TContext>>();
        var jobLock = sp.GetRequiredService<IJobLock>();
        var executor = sp.GetRequiredService<JobExecutor<TContext>>();

        await using var dc = await dcFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        // pick：启用 + 到期（NextRunAt <= now）。一次取一批，避免长事务持锁。
        var dueJobs = await dc.Set<TenE0ScheduledJob>()
            .Where(j => j.IsEnabled && j.NextRunAt != null && j.NextRunAt <= now)
            .OrderBy(j => j.NextRunAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        if (dueJobs.Count == 0)
        {
            return 0;
        }

        var processed = 0;
        foreach (var job in dueJobs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 集群协调：抢锁。false 时本轮跳过（他人持有或租约未到），
            // 不改动任何状态 —— 由真正持有锁的实例处理。
            var acquired = await jobLock.TryAcquireAsync(
                job.Code, _options.LockInstanceId, _options.LockLeaseDuration, cancellationToken);
            if (!acquired)
            {
                logger.LogDebug("任务锁未获取，跳过 Code={Code} Instance={Instance}",
                    job.Code, _options.LockInstanceId);
                continue;
            }

            try
            {
                // executor 内部完成：重试、历史、事件、NextRunAt 更新。
                await executor.ExecuteAsync(job, _options.LockInstanceId, cancellationToken);
                processed++;
            }
            finally
            {
                // 异常路径也必须释放 —— 实现层校验 (jobCode, instanceId) 后清空，
                // 不属于本实例的锁由实现层拒绝，绝不误删他实例锁。
                await jobLock.ReleaseAsync(job.Code, _options.LockInstanceId, cancellationToken);
            }
        }

        // 可观测性：刷新活跃任务数（#161 / 本模块）。未注册 TenE0Metrics 时 no-op。
        if (sp.GetService<TenE0Metrics>() is { } metrics)
        {
            var active = await dc.Set<TenE0ScheduledJob>()
                .CountAsync(j => j.IsEnabled && j.NextRunAt != null, cancellationToken);
            metrics.SetJobActive(active);
        }

        return processed;
    }
}
