namespace TenE0.Core.Scheduling;

/// <summary>
/// 单次任务执行的结果状态（issue #164），记录在 <see cref="Entities.TenE0JobExecution.Status"/>
/// 与 <see cref="Entities.TenE0ScheduledJob.LastRunStatus"/>。
///
/// <para>
/// 取值约定：枚举 int 值仅追加，禁止重排已有值（向后兼容老配置）。
/// </para>
/// </summary>
public enum JobExecutionStatus
{
    /// <summary>运行中（尚未结束）。</summary>
    Running = 0,

    /// <summary>执行成功。</summary>
    Success = 1,

    /// <summary>执行失败（重试耗尽或不可重试异常）。</summary>
    Failed = 2,

    /// <summary>执行超时（超过 <c>SchedulingOptions.JobTimeout</c>）。</summary>
    Timeout = 3,
}
