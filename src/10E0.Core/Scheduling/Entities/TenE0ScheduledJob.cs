using TenE0.Core.Abstractions;
using TenE0.Core.Entities;

namespace TenE0.Core.Scheduling.Entities;

/// <summary>
/// 定时任务定义（issue #164）—— 持久化的调度元数据。
///
/// <para>
/// 一个 <see cref="TenE0ScheduledJob"/> 行描述「什么任务、什么时候跑、跑多久、重试几次」。
/// <see cref="SchedulerWorker{TContext}"/> 后台扫描 <see cref="NextRunAt"/> &lt;= now 的行，
/// 抢锁后交由 <see cref="JobExecutor{TContext}"/> 执行。
/// </para>
///
/// <para>
/// <b>两种注册方式</b>（<see cref="Mode"/>）：
/// <list type="bullet">
/// <item><see cref="JobExecutionMode.Static"/>：<c>[Scheduled]</c> attribute 标记，启动期扫描注册。</item>
/// <item><see cref="JobExecutionMode.Dynamic"/>：Admin API 运行时增删改。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>集群协调</b>（<see cref="LockedByInstance"/> / <see cref="LockedUntil"/>）：
/// 复用 Outbox 行级锁模式（#80/#81）。多实例部署时同一任务只在一个实例执行 ——
/// <see cref="RowJobLock{TContext}"/> 用 <c>UPDATE ... WHERE (LockedByInstance IS NULL OR LockedUntil &lt;= now)</c>
/// 抢占，租约过期后另一实例可接管。
/// </para>
/// </summary>
public sealed class TenE0ScheduledJob : AuditedEntity, IMultiTenantEntity
{
    /// <summary>
    /// 业务编码（唯一）。静态任务由 <c>[Scheduled]</c> 类名推导；动态任务由调用方指定。
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>展示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Cron 表达式（如 <c>"0 0 9 * * ?"</c> 每天 9 点）。
    /// 由 <see cref="CronExtensions"/> 解析。
    /// </summary>
    public string CronExpression { get; set; } = string.Empty;

    /// <summary>
    /// 任务处理器类型全名（含程序集），格式 <c>"Namespace.Type, Assembly"</c>。
    /// 动态任务通过反射加载，受白名单程序集约束（防止任意代码执行）。
    /// </summary>
    public string JobType { get; set; } = string.Empty;

    /// <summary>
    /// 任务参数（JSON）。由 <see cref="IScheduledJob"/> 在执行时反序列化使用。
    /// </summary>
    public string? ParametersJson { get; set; }

    /// <summary>是否启用。禁用的任务不被 <see cref="SchedulerWorker{TContext}"/> 拾取。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>注册方式（静态 / 动态）。</summary>
    public JobExecutionMode Mode { get; set; } = JobExecutionMode.Static;

    /// <summary>最大重试次数（含首次执行）。默认 3。</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>重试间隔。默认 1 分钟。</summary>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>上次执行时间。</summary>
    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>
    /// 下次执行时间。<= now 即视为到期，<see cref="SchedulerWorker{TContext}"/> 拾取执行。
    /// 执行完成后由 <see cref="JobExecutor{TContext}"/> 用 Cron 重新计算。
    /// </summary>
    public DateTimeOffset? NextRunAt { get; set; }

    /// <summary>
    /// 上次执行结果状态（字符串形式：Success / Failed / Timeout / Running）。
    /// 用字符串而非枚举，便于历史排障与跨版本兼容。
    /// </summary>
    public string? LastRunStatus { get; set; }

    /// <summary>
    /// 持有该任务执行锁的实例 ID；NULL 表示未锁。
    /// 集群协调用（与 Outbox <c>LockedByInstance</c> 同款语义）。
    /// </summary>
    public string? LockedByInstance { get; set; }

    /// <summary>
    /// 任务执行锁租约到期时间；NULL 表示未被任何实例拾取。
    /// 业务约定：LockedUntil &lt;= now 即视为锁过期（任何实例可重新拾取）。
    /// </summary>
    public DateTimeOffset? LockedUntil { get; set; }

    /// <inheritdoc />
    public string TenantId { get; set; } = string.Empty;
}
