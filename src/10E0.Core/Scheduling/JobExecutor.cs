using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenE0.Core.Events;
using TenE0.Core.Observability;
using TenE0.Core.Scheduling.Entities;

namespace TenE0.Core.Scheduling;

/// <summary>
/// 任务执行器（issue #164）—— 从 DI 解析 <see cref="IScheduledJob"/>，按重试策略执行，
/// 记录执行历史，重试耗尽时触发 <see cref="JobFailedEvent"/>，并更新下次执行时间。
///
/// <para>
/// 职责拆分（与 <c>OutboxRelayService</c> 同款）：
/// <list type="bullet">
/// <item>本类只负责「解析 handler → 重试 → 记录历史 → 触发事件 → 更新 NextRunAt」。</item>
/// <item>何时触发由 <see cref="SchedulerWorker{TContext}"/> 决定（到期扫描 + 抢锁）。</item>
/// <item>handler 的具体逻辑由 <see cref="IScheduledJob"/> 实现决定。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>历史记录粒度</b>：每次完整执行（含内部所有重试）写<b>一行</b> <see cref="TenE0JobExecution"/>，
/// Attempt 字段记录最终成功/失败的尝试序号。这样历史表不会因重试爆炸，同时仍保留重试信息。
/// </para>
/// </summary>
/// <typeparam name="TContext">承载任务表的 EF Core DbContext 类型。</typeparam>
public sealed class JobExecutor<TContext>(
    IServiceProvider serviceProvider,
    IOptions<SchedulingOptions> options,
    TimeProvider timeProvider,
    ILogger<JobExecutor<TContext>> logger) : IDisposable
    where TContext : DbContext
{
    private readonly SchedulingOptions _options = options.Value;
    private CancellationTokenSource? _timeoutCts;

    /// <summary>
    /// 执行一个任务（含重试、历史记录、事件触发、NextRunAt 更新）。
    /// </summary>
    /// <param name="job">要执行的任务定义。</param>
    /// <param name="instanceId">执行实例 ID（集群协调用，写入历史 InstanceId）。</param>
    /// <param name="cancellationToken">外部取消令牌（通常来自 BackgroundService 生命周期）。</param>
    /// <returns>执行结果状态。</returns>
    public async Task<JobExecutionStatus> ExecuteAsync(
        TenE0ScheduledJob job,
        string instanceId,
        CancellationToken cancellationToken)
    {
        // 1. 解析 handler —— JobType 受白名单校验（防反射注入任意代码）。
        IScheduledJob handler;
        try
        {
            handler = ResolveJobHandler(job.JobType);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "无法解析任务 handler Code={Code} JobType={JobType}", job.Code, job.JobType);
            var now0 = timeProvider.GetUtcNow();
            await RecordExecutionAsync(job.Id, instanceId, JobExecutionStatus.Failed, attempt: 0,
                errorMessage: $"无法解析 handler：{Truncate(ex.Message, 2000)}", startedAt: now0, finishedAt: now0,
                cancellationToken);
            await UpdateJobAfterRunAsync(job, JobExecutionStatus.Failed, cancellationToken);
            return JobExecutionStatus.Failed;
        }

        // 2. 创建执行历史行（Running）
        var startedAt = timeProvider.GetUtcNow();
        var execution = new TenE0JobExecution
        {
            JobId = job.Id,
            StartedAt = startedAt,
            Status = JobExecutionStatus.Running.ToString(),
            Attempt = 1,
            InstanceId = instanceId,
        };
        await using (var seedScope = serviceProvider.CreateAsyncScope())
        {
            var seedFactory = seedScope.ServiceProvider.GetRequiredService<IDbContextFactory<TContext>>();
            await using var seedCtx = await seedFactory.CreateDbContextAsync(cancellationToken);
            seedCtx.Set<TenE0JobExecution>().Add(execution);
            await seedCtx.SaveChangesAsync(cancellationToken);
        }

        // 3. 带超时的 CancellationTokenSource（外部 token + 超时 token，任一触发即取消）。
        _timeoutCts?.Dispose();
        _timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timeoutCts.CancelAfter(_options.JobTimeout);

        var finalStatus = JobExecutionStatus.Failed;
        var finalAttempt = 1;
        string? finalError = null;

        for (var attempt = 1; attempt <= job.MaxRetries; attempt++)
        {
            finalAttempt = attempt;
            var ct = _timeoutCts.Token;
            try
            {
                var context = new JobContext(job, attempt, parameters: null);
                await handler.ExecuteAsync(context, ct);
                finalStatus = JobExecutionStatus.Success;
                finalError = null;
                break;
            }
            catch (OperationCanceledException) when (_timeoutCts.IsCancellationRequested
                                                     && !cancellationToken.IsCancellationRequested)
            {
                // 超时取消（非外部关闭）：标记 Timeout，不再重试。
                finalStatus = JobExecutionStatus.Timeout;
                finalError = $"任务超时（{_options.JobTimeout}）";
                break;
            }
            catch (Exception ex) when (attempt < job.MaxRetries)
            {
                // 非末次失败：记录错误，等待 RetryInterval 后重试。
                finalError = Truncate(ex.Message, 2000);
                logger.LogWarning(ex, "任务执行失败 Code={Code} Attempt={Attempt}/{Max}，将重试",
                    job.Code, attempt, job.MaxRetries);
                try
                {
                    await Task.Delay(job.RetryInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // 重试等待期被外部取消：标记 Failed 并退出。
                    finalStatus = JobExecutionStatus.Failed;
                    finalError = $"任务被取消（重试等待期）：{Truncate(ex.Message, 2000)}";
                    break;
                }
            }
            catch (Exception ex)
            {
                // 末次失败：记录错误，退出循环。
                finalStatus = JobExecutionStatus.Failed;
                finalError = Truncate(ex.Message, 2000);
                logger.LogError(ex, "任务重试耗尽 Code={Code} Attempt={Attempt}/{Max}",
                    job.Code, attempt, job.MaxRetries);
            }
        }

        // 4. 更新历史行（Running → 最终状态）
        var finishedAt = timeProvider.GetUtcNow();
        await RecordExecutionFinalAsync(execution.Id, finalStatus, finalAttempt, finalError, finishedAt, cancellationToken);

        // 5. 失败 → 触发 JobFailedEvent（经 Outbox 异步分发；未注册 dispatcher 时 no-op）
        if (finalStatus is JobExecutionStatus.Failed or JobExecutionStatus.Timeout)
        {
            await RaiseJobFailedEventAsync(job, finalAttempt, finalError ?? "未知错误", cancellationToken);
        }

        // 6. 更新任务定义：LastRunAt / LastRunStatus / NextRunAt（Cron 重新计算）
        await UpdateJobAfterRunAsync(job, finalStatus, cancellationToken);

        // 可观测性埋点（#161 / 本模块）：未注册 TenE0Metrics 时为 null → no-op。
        var metrics = serviceProvider.GetService<TenE0Metrics>();
        metrics?.JobExecuted.Add(1, [new(TenE0Metrics.Tags.JobCode, job.Code),
            new(TenE0Metrics.Tags.Result, finalStatus == JobExecutionStatus.Success
                ? TenE0Metrics.Tags.Success : TenE0Metrics.Tags.Failure)]);

        return finalStatus;
    }

    /// <summary>
    /// 从 DI 解析 <see cref="IScheduledJob"/> 实例，并校验 <paramref name="jobTypeName"/>
    /// 实现本接口且所在程序集在白名单内。
    /// </summary>
    internal IScheduledJob ResolveJobHandler(string jobTypeName)
    {
        if (string.IsNullOrWhiteSpace(jobTypeName))
        {
            throw new InvalidOperationException("JobType 不能为空");
        }

        var type = Type.GetType(jobTypeName, throwOnError: false);
        if (type is null)
        {
            throw new InvalidOperationException($"无法加载 JobType '{jobTypeName}'");
        }

        if (!typeof(IScheduledJob).IsAssignableFrom(type))
        {
            throw new InvalidOperationException($"JobType '{jobTypeName}' 未实现 IScheduledJob");
        }

        // 白名单校验：JobType 所在程序集必须在 AllowedAssemblies 或 JobAssemblies 内。
        // AllowedAssemblies 为空时回退到 JobAssemblies（静态任务扫描程序集），保持默认安全。
        var allowed = _options.AllowedAssemblies ?? _options.JobAssemblies;
        if (allowed is { Length: > 0 } && !allowed.Contains(type.Assembly))
        {
            throw new InvalidOperationException(
                $"JobType '{jobTypeName}' 所在程序集 {type.Assembly.GetName().Name} 不在白名单内，" +
                "拒绝加载（防反射注入任意代码）。");
        }

        // 从 DI 解析（任务注册为 Scoped）；未注册时反射 new（无参构造）。
        // 优先 DI 让任务能注入 Scoped 服务（DbContext 工厂、命令分发器等）。
        return (IScheduledJob)serviceProvider.GetRequiredService(type);
    }

    private async Task RecordExecutionAsync(
        string jobId, string instanceId, JobExecutionStatus status, int attempt,
        string? errorMessage, DateTimeOffset startedAt, DateTimeOffset? finishedAt,
        CancellationToken cancellationToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TContext>>();
        await using var ctx = await factory.CreateDbContextAsync(cancellationToken);
        ctx.Set<TenE0JobExecution>().Add(new TenE0JobExecution
        {
            JobId = jobId,
            StartedAt = startedAt,
            FinishedAt = finishedAt,
            Status = status.ToString(),
            Attempt = attempt,
            ErrorMessage = errorMessage,
            InstanceId = instanceId,
        });
        await ctx.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordExecutionFinalAsync(
        string executionId, JobExecutionStatus status, int attempt, string? errorMessage,
        DateTimeOffset finishedAt, CancellationToken cancellationToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TContext>>();
        await using var ctx = await factory.CreateDbContextAsync(cancellationToken);
        var exec = await ctx.Set<TenE0JobExecution>()
            .FirstOrDefaultAsync(e => e.Id == executionId, cancellationToken);
        if (exec is null)
        {
            return;
        }
        exec.Status = status.ToString();
        exec.Attempt = attempt;
        exec.ErrorMessage = errorMessage;
        exec.FinishedAt = finishedAt;
        await ctx.SaveChangesAsync(cancellationToken);
    }

    private async Task RaiseJobFailedEventAsync(
        TenE0ScheduledJob job, int attempt, string errorMessage, CancellationToken cancellationToken)
    {
        try
        {
            var dispatcher = serviceProvider.GetService<IDomainEventDispatcher>();
            if (dispatcher is null)
            {
                return; // 未启用领域事件 → no-op（不破坏调度核心流程）
            }
            await dispatcher.DispatchAsync(
                new JobFailedEvent(job.Id, job.Code, job.Name, attempt, errorMessage, timeProvider.GetUtcNow()),
                cancellationToken);
        }
        catch (Exception ex)
        {
            // 事件触发失败不应影响调度核心流程；仅记录。
            logger.LogWarning(ex, "触发 JobFailedEvent 失败 Code={Code}", job.Code);
        }
    }

    private async Task UpdateJobAfterRunAsync(
        TenE0ScheduledJob job, JobExecutionStatus status, CancellationToken cancellationToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TContext>>();
        await using var ctx = await factory.CreateDbContextAsync(cancellationToken);
        var tracked = await ctx.Set<TenE0ScheduledJob>()
            .FirstOrDefaultAsync(j => j.Id == job.Id, cancellationToken);
        if (tracked is null)
        {
            return;
        }
        var now = timeProvider.GetUtcNow();
        tracked.LastRunAt = now;
        tracked.LastRunStatus = status.ToString();
        // 用 Cron 重新计算下次执行时间。解析失败时 NextRunAt 置 null（任务将不再被拾取，等运维修正 Cron）。
        tracked.NextRunAt = CronExtensions.GetNextOccurrence(
            tracked.CronExpression, now, _options.TimeZone, tracked.Code);
        await ctx.SaveChangesAsync(cancellationToken);
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max];

    /// <summary>释放超时 CTS。</summary>
    public void Dispose() => _timeoutCts?.Dispose();
}
