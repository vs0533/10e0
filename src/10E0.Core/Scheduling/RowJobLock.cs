using Microsoft.EntityFrameworkCore;
using TenE0.Core.Scheduling.Entities;

namespace TenE0.Core.Scheduling;

/// <summary>
/// 数据库行级锁 provider（issue #164）—— 通过 <c>UPDATE</c> 设置
/// <see cref="TenE0ScheduledJob.LockedByInstance"/> / <see cref="TenE0ScheduledJob.LockedUntil"/>
/// 抢占任务的执行权。
///
/// <para>
/// <b>为什么是 UPDATE 而不是显式 SELECT ... WITH (UPDLOCK)？</b>
/// 与 <c>SqlServerOutboxLock</c> 同款：单条 <c>UPDATE ... WHERE (LockedByInstance IS NULL OR
/// LockedUntil &lt;= now)</c> 用排他锁天然实现互斥，影响行数 = 0 即视为抢锁失败，行为确定。
/// </para>
///
/// <para>
/// <b>双路径策略</b>（与 <c>SqlServerOutboxLock</c> 一致）：
/// 每次调用时探测当前 DbContext 的 <c>ProviderName</c> —— InMemory provider 走 LINQ 路径
/// （让单测可在内存库上跑），其他 provider 走 <c>ExecuteSqlInterpolatedAsync</c> 拼 UPDATE 路径。
/// 两条路径行为契约完全一致。
/// </para>
/// </summary>
/// <typeparam name="TContext">承载 <see cref="TenE0ScheduledJob"/> 表的 EF Core DbContext 类型。</typeparam>
public sealed class RowJobLock<TContext> : IJobLock
    where TContext : DbContext
{
    private readonly IDbContextFactory<TContext> _factory;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 构造行级锁 provider。
    /// </summary>
    /// <param name="factory">承载任务表的 DbContext 工厂；每次调用都新建 DbContext，避免共享 DbContext 的线程安全陷阱。</param>
    /// <param name="timeProvider">当前时间来源（测试用 <c>FakeTimeProvider</c> 控制时间）。默认 <see cref="TimeProvider.System"/>。</param>
    public RowJobLock(IDbContextFactory<TContext> factory, TimeProvider? timeProvider = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(
        string jobCode,
        string instanceId,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobCode))
        {
            return false;
        }

        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var newLockedUntil = now + lease;

        // InMemory provider：走 LINQ 路径（InMemory 不支持 ExecuteSqlInterpolatedAsync 写库）
        if (IsInMemoryProvider(ctx))
        {
            var job = await ctx.Set<TenE0ScheduledJob>()
                .FirstOrDefaultAsync(j => j.Code == jobCode, cancellationToken);
            if (job is null)
            {
                return false;
            }

            // 持锁条件：被其他实例持有且租约未到期。
            // 自持自取允许覆盖（与 SQL 路径 "LockedByInstance IS NULL OR LockedUntil <= now" 等价），
            // 否则同一实例在重试时会被自己卡住。
            if (job.LockedByInstance is not null
                && !string.Equals(job.LockedByInstance, instanceId, StringComparison.Ordinal)
                && job.LockedUntil > now)
            {
                return false;
            }

            job.LockedByInstance = instanceId;
            job.LockedUntil = newLockedUntil;
            await ctx.SaveChangesAsync(cancellationToken);
            return true;
        }

        // 真实关系型 provider 路径：单条 UPDATE 用 WHERE 条件天然实现
        // "未被任何实例持有" 或 "锁已过期" 即可抢占。
        // ExecuteSqlInterpolatedAsync 自动把插值参数参数化（防 SQL 注入）；
        // 返回受影响行数；0 行 = 抢锁失败，1 行 = 抢占成功。
        var rows = await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE TenE0ScheduledJobs
                SET LockedByInstance = {instanceId},
                    LockedUntil = {newLockedUntil}
              WHERE Code = {jobCode}
                AND (LockedByInstance IS NULL OR LockedUntil <= {now})
             """,
            cancellationToken);

        return rows > 0;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(
        string jobCode,
        string instanceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobCode))
        {
            return;
        }

        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);

        // InMemory provider：走 LINQ 路径 + 所有权校验
        if (IsInMemoryProvider(ctx))
        {
            var job = await ctx.Set<TenE0ScheduledJob>()
                .FirstOrDefaultAsync(j => j.Code == jobCode, cancellationToken);
            if (job is null)
            {
                return;
            }

            // 所有权校验：仅当 LockedByInstance == 调用方 instanceId 时才清空；
            // 避免误释放其他实例持有的锁（契约要求幂等且不抛异常）。
            if (!string.Equals(job.LockedByInstance, instanceId, StringComparison.Ordinal))
            {
                return;
            }

            job.LockedByInstance = null;
            job.LockedUntil = null;
            await ctx.SaveChangesAsync(cancellationToken);
            return;
        }

        // 真实关系型 provider 路径：所有权校验通过 WHERE 子句实现 ——
        // 其他实例持有的行 LockedByInstance != instanceId，UPDATE 命中 0 行，效果等同 no-op。
        await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE TenE0ScheduledJobs
                SET LockedByInstance = NULL,
                    LockedUntil = NULL
              WHERE Code = {jobCode}
                AND LockedByInstance = {instanceId}
             """,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> IsRunningAsync(string jobCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobCode))
        {
            return false;
        }

        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
        var now = _timeProvider.GetUtcNow();

        // 任务「正在执行」= 被某实例锁住且租约未到期。
        return await ctx.Set<TenE0ScheduledJob>()
            .AnyAsync(j => j.Code == jobCode
                && j.LockedByInstance != null
                && j.LockedUntil > now, cancellationToken);
    }

    /// <summary>
    /// 探测当前 DbContext 底层是否 InMemory provider ——
    /// InMemory 不支持 <c>ExecuteSqlInterpolatedAsync</c> 写库，必须走 LINQ 路径。
    /// </summary>
    private static bool IsInMemoryProvider(TContext ctx) =>
        (ctx.Database.ProviderName ?? string.Empty)
            .Contains("InMemory", StringComparison.OrdinalIgnoreCase);
}
