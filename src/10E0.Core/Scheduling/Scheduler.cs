using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TenE0.Core.Scheduling.Entities;

namespace TenE0.Core.Scheduling;

/// <summary>
/// <see cref="IScheduler"/> 的泛型实现（issue #164）。
///
/// <para>
/// Scoped 生命周期：每次 Admin API 请求一个 scope，从 <see cref="IDbContextFactory{TContext}"/>
/// 创建独立 DbContext。Cron 校验复用 <see cref="CronExtensions"/>，白名单校验复用
/// <see cref="JobExecutor{TContext}.ResolveJobHandler"/> 的同款逻辑（不重复实现）。
/// </para>
/// </summary>
/// <typeparam name="TContext">承载任务表的 EF Core DbContext 类型。</typeparam>
public sealed class Scheduler<TContext>(
    IDbContextFactory<TContext> factory,
    IJobLock jobLock,
    IOptions<SchedulingOptions> options,
    TimeProvider timeProvider) : IScheduler
    where TContext : DbContext
{
    private readonly SchedulingOptions _options = options.Value;

    /// <inheritdoc />
    public async Task<IReadOnlyList<TenE0ScheduledJob>> ListJobsAsync(CancellationToken cancellationToken)
    {
        await using var ctx = await factory.CreateDbContextAsync(cancellationToken);
        return await ctx.Set<TenE0ScheduledJob>()
            .AsNoTracking()
            .OrderBy(j => j.Code)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TenE0ScheduledJob?> GetJobAsync(string id, CancellationToken cancellationToken)
    {
        await using var ctx = await factory.CreateDbContextAsync(cancellationToken);
        return await ctx.Set<TenE0ScheduledJob>()
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TenE0ScheduledJob> CreateJobAsync(
        string code, string name, string cronExpression, string jobType,
        string? parametersJson, int maxRetries, TimeSpan retryInterval,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobType);
        if (!CronExtensions.IsValid(cronExpression))
        {
            throw new InvalidOperationException($"无效的 Cron 表达式 '{cronExpression}'");
        }
        // 白名单 + 接口校验（防反射注入任意代码）。
        ValidateJobType(jobType);

        await using var ctx = await factory.CreateDbContextAsync(cancellationToken);

        // Code 全局唯一校验。
        var exists = await ctx.Set<TenE0ScheduledJob>().AnyAsync(j => j.Code == code, cancellationToken);
        if (exists)
        {
            throw new InvalidOperationException($"任务 Code '{code}' 已存在");
        }

        var now = timeProvider.GetUtcNow();
        var job = new TenE0ScheduledJob
        {
            Code = code,
            Name = name,
            CronExpression = cronExpression,
            JobType = jobType,
            ParametersJson = parametersJson,
            IsEnabled = true,
            Mode = JobExecutionMode.Dynamic,
            MaxRetries = maxRetries > 0 ? maxRetries : 3,
            RetryInterval = retryInterval > TimeSpan.Zero ? retryInterval : TimeSpan.FromMinutes(1),
            NextRunAt = CronExtensions.GetNextOccurrence(cronExpression, now, _options.TimeZone, code),
        };
        ctx.Set<TenE0ScheduledJob>().Add(job);
        await ctx.SaveChangesAsync(cancellationToken);
        return job;
    }

    /// <inheritdoc />
    public async Task<TenE0ScheduledJob> UpdateJobAsync(
        string id, string? name, string? cronExpression, bool? isEnabled,
        string? parametersJson, int? maxRetries, TimeSpan? retryInterval,
        CancellationToken cancellationToken)
    {
        await using var ctx = await factory.CreateDbContextAsync(cancellationToken);
        var job = await ctx.Set<TenE0ScheduledJob>()
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"任务 {id} 不存在");

        if (job.Mode == JobExecutionMode.Static)
        {
            throw new InvalidOperationException(
                $"静态任务 '{job.Code}' 不可通过 API 修改（其定义在代码中，改代码后重启生效）");
        }

        if (cronExpression is not null)
        {
            if (!CronExtensions.IsValid(cronExpression))
            {
                throw new InvalidOperationException($"无效的 Cron 表达式 '{cronExpression}'");
            }
            job.CronExpression = cronExpression;
            // 改 Cron 后重算下次执行（按当前时间）。
            job.NextRunAt = CronExtensions.GetNextOccurrence(
                cronExpression, timeProvider.GetUtcNow(), _options.TimeZone, job.Code);
        }
        if (name is not null) job.Name = name;
        if (isEnabled is not null) job.IsEnabled = isEnabled.Value;
        if (parametersJson is not null) job.ParametersJson = parametersJson;
        if (maxRetries is not null && maxRetries > 0) job.MaxRetries = maxRetries.Value;
        if (retryInterval is not null && retryInterval > TimeSpan.Zero) job.RetryInterval = retryInterval.Value;

        await ctx.SaveChangesAsync(cancellationToken);
        return job;
    }

    /// <inheritdoc />
    public async Task<bool> TriggerJobAsync(string id, CancellationToken cancellationToken)
    {
        await using var ctx = await factory.CreateDbContextAsync(cancellationToken);
        var job = await ctx.Set<TenE0ScheduledJob>()
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
        if (job is null)
        {
            return false;
        }
        // 若任务正被某实例执行，拒绝手动触发（避免重叠）。
        if (await jobLock.IsRunningAsync(job.Code, cancellationToken))
        {
            return false;
        }
        // 置 NextRunAt = now，下轮 SchedulerWorker 扫描即拾取。
        job.NextRunAt = timeProvider.GetUtcNow();
        await ctx.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> EnableJobAsync(string id, CancellationToken cancellationToken)
    {
        await using var ctx = await factory.CreateDbContextAsync(cancellationToken);
        var job = await ctx.Set<TenE0ScheduledJob>()
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
        if (job is null) return false;
        if (job.IsEnabled) return true;
        job.IsEnabled = true;
        // 启用时若 NextRunAt 已过，重算避免立即过期堆积。
        if (job.NextRunAt is null || job.NextRunAt <= timeProvider.GetUtcNow())
        {
            job.NextRunAt = CronExtensions.GetNextOccurrence(
                job.CronExpression, timeProvider.GetUtcNow(), _options.TimeZone, job.Code);
        }
        await ctx.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DisableJobAsync(string id, CancellationToken cancellationToken)
    {
        await using var ctx = await factory.CreateDbContextAsync(cancellationToken);
        var job = await ctx.Set<TenE0ScheduledJob>()
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
        if (job is null) return false;
        job.IsEnabled = false;
        await ctx.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TenE0JobExecution>> GetExecutionsAsync(
        string jobId, int limit, CancellationToken cancellationToken)
    {
        await using var ctx = await factory.CreateDbContextAsync(cancellationToken);
        var safeLimit = limit > 0 && limit <= 500 ? limit : 50;
        return await ctx.Set<TenE0JobExecution>()
            .AsNoTracking()
            .Where(e => e.JobId == jobId)
            .OrderByDescending(e => e.StartedAt)
            .Take(safeLimit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// 校验 JobType 实现了 <see cref="IScheduledJob"/> 且在白名单程序集内。
    /// 与 <see cref="JobExecutor{TContext}.ResolveJobHandler"/> 同款逻辑，但创建期就校验
    /// （早失败，避免存了非法 JobType 运行时才崩）。
    /// </summary>
    private void ValidateJobType(string jobTypeName)
    {
        var type = Type.GetType(jobTypeName, throwOnError: false)
            ?? throw new InvalidOperationException($"无法加载 JobType '{jobTypeName}'");
        if (!typeof(IScheduledJob).IsAssignableFrom(type))
        {
            throw new InvalidOperationException($"JobType '{jobTypeName}' 未实现 IScheduledJob");
        }
        // 白名单 fail-secure（语义同 JobExecutor.ResolveJobHandler）：
        //   AllowedAssemblies == null → 用 JobAssemblies；都为空 → 不限制；
        //   配置了但为空 [] 或不含本程序集 → 拒绝。
        Assembly[]? allowed = _options.AllowedAssemblies ?? (_options.JobAssemblies.Length > 0 ? _options.JobAssemblies : null);
        if (allowed is not null && !allowed.Contains(type.Assembly))
        {
            throw new InvalidOperationException(
                $"JobType '{jobTypeName}' 所在程序集 {type.Assembly.GetName().Name} 不在白名单内，" +
                "拒绝创建（防反射注入任意代码）。");
        }
    }
}
