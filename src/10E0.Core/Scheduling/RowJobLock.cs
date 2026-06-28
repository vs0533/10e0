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
        // 返回受影响行数；0 行 = 抢锁失败，1 行 = 抢占成功。
        //
        // 表名从 EF Core 模型元数据读（GetTableName），不硬编码 —— 实体改名 / ToTable 约定
        // 调整时原始 SQL 与 LINQ 映射始终一致，避免复数/单数漂移导致 "Invalid object name" 运行期崩溃。
        //
        // 注意：SQL 不允许表名走参数占位符（UPDATE @p0 ... 非法），所以表名必须内联到 SQL 文本。
        // tableName 来自 EF 元数据（本模块 ToTable 固定的受控常量），非用户输入，无注入风险。
        // 值（instanceId/newLockedUntil/jobCode/now）走显式 DbParameter 防注入。
        var tableName = ResolveTableName(ctx);
        var sql = $"""
                   UPDATE {tableName}
                      SET LockedByInstance = @instanceId,
                          LockedUntil = @lockedUntil
                    WHERE Code = @jobCode
                      AND (LockedByInstance IS NULL OR LockedUntil <= @now)
                   """;
        var rows = await ctx.Database.ExecuteSqlRawAsync(sql,
            new[]
            {
                CreateParam(ctx, "@instanceId", instanceId),
                CreateParam(ctx, "@lockedUntil", newLockedUntil),
                CreateParam(ctx, "@jobCode", jobCode),
                CreateParam(ctx, "@now", now),
            },
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
        // 表名内联（受控常量，见 TryAcquire 注释），值走参数。
        var tableName = ResolveTableName(ctx);
        var sql = $"""
                   UPDATE {tableName}
                      SET LockedByInstance = NULL,
                          LockedUntil = NULL
                    WHERE Code = @jobCode
                      AND LockedByInstance = @instanceId
                   """;
        await ctx.Database.ExecuteSqlRawAsync(sql,
            new[]
            {
                CreateParam(ctx, "@jobCode", jobCode),
                CreateParam(ctx, "@instanceId", instanceId),
            },
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
    /// InMemory 不支持 <c>ExecuteSqlRawAsync</c> 写库，必须走 LINQ 路径。
    /// </summary>
    private static bool IsInMemoryProvider(TContext ctx) =>
        (ctx.Database.ProviderName ?? string.Empty)
            .Contains("InMemory", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 从 EF Core 模型元数据读 <see cref="TenE0ScheduledJob"/> 的实际表名 ——
    /// 不硬编码表名，避免实体改名 / ToTable 约定调整后原始 SQL 与 LINQ 映射漂移
    /// （复数/单数差异曾导致 "Invalid object name" 运行期崩溃，PR #180 review Critical #1）。
    /// </summary>
    private static string ResolveTableName(TContext ctx)
    {
        var entityType = ctx.Model.FindEntityType(typeof(TenE0ScheduledJob))
            ?? throw new InvalidOperationException(
                "TenE0ScheduledJob 未在 DbContext 模型中注册，无法解析表名。");
        // GetTableName() 返回 ToTable 配置的权威表名（本模块固定为 "TenE0ScheduledJobs"）。
        return entityType.GetTableName()
            ?? throw new InvalidOperationException("无法从 EF 元数据解析 TenE0ScheduledJob 表名。");
    }

    /// <summary>
    /// 用底层 ADO.NET 连接创建参数（provider 无关：SqlServer/Sqlite/Postgres 各自的 DbParameter）。
    /// 表名无法参数化（SQL 语法限制），但值必须参数化防注入。
    /// </summary>
    private static System.Data.Common.DbParameter CreateParam(TContext ctx, string name, object value)
    {
        // 通过 DbConnection.CreateCommand().CreateParameter() 拿到当前 provider 的参数类型，
        // 避免硬依赖 Microsoft.Data.SqlClient / Microsoft.Data.Sqlite。
        var param = ctx.Database.GetDbConnection().CreateCommand().CreateParameter();
        param.ParameterName = name;
        param.Value = value;
        return param;
    }
}
