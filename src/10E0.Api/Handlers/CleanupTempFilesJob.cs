using TenE0.Core.Scheduling;

namespace TenE0.Api.Handlers;

/// <summary>
/// 示例静态定时任务（issue #164）—— 演示 <c>[Scheduled]</c> attribute + <see cref="ScheduledJobBase"/> 用法。
///
/// <para>
/// 这个任务每天凌晨 2:00 触发，模拟清理临时文件。真实项目里把 ExecuteJobAsync 换成
/// 实际清理逻辑（删本地文件 / OSS 前缀 / 数据库过期行）。任务会自动注册到
/// <c>TenE0ScheduledJob</c> 表（Code = 本类全名），可在 <c>GET /admin/scheduler/jobs</c> 看到。
/// </para>
///
/// <para>
/// <b>注意</b>：cron 用短间隔 <c>"0 * * * * ?"</c>（每分钟）仅便于 demo 观察；生产应改为
/// <c>"0 0 2 * * ?"</c>（每天 2:00）。
/// </para>
/// </summary>
[Scheduled("0 * * * * ?", Description = "示例：清理临时文件（每分钟，demo）", MaxRetries = 2)]
public sealed class CleanupTempFilesJob : ScheduledJobBase
{
    private readonly ILogger<CleanupTempFilesJob> _logger;

    // 从 DI 注入（SchedulerExtensions.RegisterScheduledJobHandlers 注册为 Scoped）。
    // 真实任务可注入 IDbContextFactory / ICommandDispatcher 等 Scoped 服务。
    public CleanupTempFilesJob(ILogger<CleanupTempFilesJob> logger)
    {
        _logger = logger;
        SetLogger(logger);
    }

    /// <inheritdoc />
    protected override Task ExecuteJobAsync(JobContext context, CancellationToken cancellationToken)
    {
        // 示例：仅记录日志。真实清理逻辑替换此处。
        _logger.LogInformation(
            "CleanupTempFilesJob 执行：Code={Code} Attempt={Attempt}（示例占位，无实际清理）",
            context.Job.Code, context.Attempt);
        return Task.CompletedTask;
    }
}
