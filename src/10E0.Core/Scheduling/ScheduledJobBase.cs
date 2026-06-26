using Microsoft.Extensions.Logging;

namespace TenE0.Core.Scheduling;

/// <summary>
/// <see cref="IScheduledJob"/> 的便利基类（issue #164）。
///
/// <para>
/// 提供默认日志记录样板，让具体任务实现只关心 <see cref="ExecuteAsync"/>。
/// 继承非强制 —— 直接实现 <see cref="IScheduledJob"/> 也可。
/// </para>
/// </summary>
public abstract class ScheduledJobBase : IScheduledJob
{
    /// <summary>任务执行时的日志记录器（可选；未注入时为 null，子类需判空）。</summary>
    protected ILogger? Logger { get; set; }

    /// <summary>
    /// 设置日志记录器（由 DI 构造或子类注入）。可选；不调则 <see cref="Logger"/> 为 null。
    /// </summary>
    protected void SetLogger(ILogger? logger) => Logger = logger;

    /// <summary>
    /// 执行任务的具体逻辑，由子类实现。
    /// </summary>
    /// <param name="context">执行上下文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    protected abstract Task ExecuteJobAsync(JobContext context, CancellationToken cancellationToken);

    /// <inheritdoc />
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        Logger?.LogInformation(
            "执行定时任务 Code={Code} Attempt={Attempt}", context.Job.Code, context.Attempt);
        try
        {
            await ExecuteJobAsync(context, cancellationToken);
            Logger?.LogInformation(
                "定时任务执行成功 Code={Code} Attempt={Attempt}", context.Job.Code, context.Attempt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Logger?.LogWarning(
                "定时任务被取消 Code={Code} Attempt={Attempt}", context.Job.Code, context.Attempt);
            throw;
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex,
                "定时任务执行失败 Code={Code} Attempt={Attempt}", context.Job.Code, context.Attempt);
            throw;
        }
    }
}
