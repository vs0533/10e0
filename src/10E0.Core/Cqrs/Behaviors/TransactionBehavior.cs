using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TenE0.Core.Abstractions;

namespace TenE0.Core.Cqrs.Behaviors;

/// <summary>
/// 标记命令需要事务包裹。实现此接口的命令会被 <see cref="TransactionBehavior{TCommand, TResult, TContext}"/> 自动加事务。
/// </summary>
public interface ITransactional
{
}

/// <summary>
/// 事务行为 — 替代旧 CommandManager.Dispatch() 的嵌套事务循环（已修复 BUG-001 两个问题）：
///
/// 1. 旧实现 if (beforSaveChange != null) 分支内执行命令后，分支外还有一行裸 _mediator.Send(item)
///    → 命令被执行两次。新管道每个命令只走一次。
///
/// 2. 旧实现外层 BeginTransaction 后内层又 BeginTransaction（嵌套事务在 SQL Server 上行为不确定）。
///    新实现：检测到外层已有事务则用 Savepoint 实现真正的嵌套语义，否则开新事务。
///
/// 仅对实现 ITransactional 的命令生效，避免无谓的事务开销。
/// 泛型参数 TContext 让一个项目可注册多个 DbContext 的事务行为。
/// </summary>
public sealed class TransactionBehavior<TCommand, TResult, TContext>(
    IDbContextFactory<TContext> contextFactory,
    ILogger<TransactionBehavior<TCommand, TResult, TContext>> logger)
    : IPipelineBehavior<TCommand, TResult>
    where TCommand : ICommand<TResult>
    where TContext : DbContext
{
    public async Task<TResult> HandleAsync(
        TCommand command,
        CommandHandlerDelegate<TResult> next,
        CancellationToken cancellationToken)
    {
        // 非事务命令直接放行
        if (command is not ITransactional)
            return await next(cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var database = context.Database;

        if (database.CurrentTransaction is null)
        {
            // 没有外层事务：开新事务
            await using var tx = await database.BeginTransactionAsync(cancellationToken);
            try
            {
                var result = await next(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return result;
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }
        }
        else
        {
            // 已有外层事务：用 Savepoint 实现真正的嵌套事务语义
            var savepoint = $"sp_{Guid.NewGuid():N}";
            var currentTx = database.CurrentTransaction;
            await currentTx.CreateSavepointAsync(savepoint, cancellationToken);
            try
            {
                var result = await next(cancellationToken);
                await currentTx.ReleaseSavepointAsync(savepoint, cancellationToken);
                return result;
            }
            catch
            {
                logger.LogWarning("Rolling back to savepoint {Savepoint} for {Command}", savepoint, typeof(TCommand).Name);
                await currentTx.RollbackToSavepointAsync(savepoint, cancellationToken);
                throw;
            }
        }
    }
}
