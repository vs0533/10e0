namespace TenE0.Core.Abstractions;

/// <summary>
/// 管道下一节点委托。
/// </summary>
public delegate Task<TResult> CommandHandlerDelegate<TResult>(CancellationToken cancellationToken);

/// <summary>
/// 命令管道行为（拦截器）。
///
/// 用法：日志、验证、事务、权限拦截等横切关注点。
/// 注册顺序即执行顺序（先注册的在外层），与 ASP.NET Core 中间件管道一致。
///
/// 替代旧 MediatR.IPipelineBehavior — 签名兼容，迁移成本低。
/// </summary>
public interface IPipelineBehavior<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(
        TCommand command,
        CommandHandlerDelegate<TResult> next,
        CancellationToken cancellationToken);
}
