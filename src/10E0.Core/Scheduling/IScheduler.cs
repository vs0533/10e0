using TenE0.Core.Scheduling.Entities;

namespace TenE0.Core.Scheduling;

/// <summary>
/// 调度器管理契约（issue #164）—— 提供给 Admin API 的 CRUD + 手动触发操作。
///
/// <para>
/// 与 <see cref="SchedulerWorker{TContext}"/> 区分：
/// <list type="bullet">
/// <item><see cref="IScheduler"/> = 「管理面」（创建/查询/触发/禁用任务），由 Admin endpoint 调用。</item>
/// <item><see cref="SchedulerWorker{TContext}"/> = 「数据面」（扫描到期任务并执行），后台 BackgroundService。</item>
/// </list>
/// </para>
/// </summary>
public interface IScheduler
{
    /// <summary>列出所有任务（按 Code 排序），返回投影（避免泄露不需要的字段）。</summary>
    Task<IReadOnlyList<TenE0ScheduledJob>> ListJobsAsync(CancellationToken cancellationToken);

    /// <summary>按 Id 取单个任务；不存在返回 null。</summary>
    Task<TenE0ScheduledJob?> GetJobAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// 创建动态任务。<paramref name="jobType"/> 必须实现 <see cref="IScheduledJob"/> 且在白名单程序集内。
    /// </summary>
    /// <exception cref="InvalidOperationException">Code 已存在 / Cron 非法 / JobType 不合规。</exception>
    Task<TenE0ScheduledJob> CreateJobAsync(
        string code, string name, string cronExpression, string jobType,
        string? parametersJson, int maxRetries, TimeSpan retryInterval,
        CancellationToken cancellationToken);

    /// <summary>更新动态任务（Cron / 启用 / 参数 / 重试策略）。静态任务不可改（抛异常）。</summary>
    /// <exception cref="InvalidOperationException">任务不存在 / 静态任务 / Cron 非法。</exception>
    Task<TenE0ScheduledJob> UpdateJobAsync(
        string id, string? name, string? cronExpression, bool? isEnabled,
        string? parametersJson, int? maxRetries, TimeSpan? retryInterval,
        CancellationToken cancellationToken);

    /// <summary>
    /// 手动立即触发任务：把 <see cref="TenE0ScheduledJob.NextRunAt"/> 置为 now，
    /// 下次 <see cref="SchedulerWorker{TContext}"/> 扫描即拾取。若任务正被某实例执行，返回 false。
    /// </summary>
    /// <returns>true 触发成功；false 任务不存在或正在执行。</returns>
    Task<bool> TriggerJobAsync(string id, CancellationToken cancellationToken);

    /// <summary>启用任务。</summary>
    Task<bool> EnableJobAsync(string id, CancellationToken cancellationToken);

    /// <summary>禁用任务（暂停调度，不删除）。</summary>
    Task<bool> DisableJobAsync(string id, CancellationToken cancellationToken);

    /// <summary>查询任务的执行历史（按时间倒序）。</summary>
    Task<IReadOnlyList<TenE0JobExecution>> GetExecutionsAsync(
        string jobId, int limit, CancellationToken cancellationToken);
}
