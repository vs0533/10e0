using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TenE0.Core.Abstractions;

namespace TenE0.Core.Cqrs.Behaviors;

/// <summary>
/// 日志行为 — 记录命令开始/结束/耗时/异常。
///
/// 与旧 LoggingBehavior 的差异：
/// - 加入耗时统计（Stopwatch）
/// - 加入异常捕获并重抛（保留堆栈），不吞错
/// - 不再依赖 IsDebug 配置才注册（旧实现仅 Debug 模式有日志）
/// </summary>
public sealed class LoggingBehavior<TCommand, TResult>(
    ILogger<LoggingBehavior<TCommand, TResult>> logger) : IPipelineBehavior<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<TResult> HandleAsync(
        TCommand command,
        CommandHandlerDelegate<TResult> next,
        CancellationToken cancellationToken)
    {
        var commandName = typeof(TCommand).Name;
        var sw = Stopwatch.StartNew();

        logger.LogInformation("Command starting: {Command}", commandName);

        try
        {
            var result = await next(cancellationToken);
            sw.Stop();
            logger.LogInformation("Command completed: {Command} in {ElapsedMs}ms", commandName, sw.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex, "Command failed: {Command} after {ElapsedMs}ms", commandName, sw.ElapsedMilliseconds);
            throw;
        }
    }
}
