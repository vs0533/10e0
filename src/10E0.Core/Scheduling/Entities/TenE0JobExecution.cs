using TenE0.Core.Entities;

namespace TenE0.Core.Scheduling.Entities;

/// <summary>
/// 任务执行历史记录（issue #164）—— 每次执行（含每次重试）写一行。
///
/// <para>
/// 与 <see cref="TenE0ScheduledJob"/> 区分：
/// <c>TenE0ScheduledJob</c> 是「任务定义」（一行一任务），<c>TenE0JobExecution</c> 是
/// 「执行记录」（一行一次执行）。查询某任务历史走
/// <c>GET /admin/scheduler/jobs/{id}/executions</c>。
/// </para>
/// </summary>
public sealed class TenE0JobExecution : BaseEntity
{
    /// <summary>所属任务 ID（<see cref="TenE0ScheduledJob.Id"/>）。</summary>
    public string JobId { get; set; } = string.Empty;

    /// <summary>本次执行开始时间。</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>本次执行结束时间；NULL 表示运行中。</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>执行结果状态（字符串形式：Running / Success / Failed / Timeout）。</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>失败原因（截断后存）；NULL 表示成功。</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>第几次尝试（1 起）。重试每次递增。</summary>
    public int Attempt { get; set; }

    /// <summary>
    /// 执行实例 ID（集群协调用）。
    /// 多实例部署时据此判断哪台机器跑的，便于排障。
    /// </summary>
    public string? InstanceId { get; set; }
}
