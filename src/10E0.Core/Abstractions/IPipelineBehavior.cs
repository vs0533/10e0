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
///
/// #41 增量：<see cref="Order"/> 控制包裹顺序。**值越大越靠外层**（先进入、最先退出）。
/// 默认 0；同 Order 的行为按注册顺序稳定排序（向后兼容旧"先注册 = 外层"约定）。
/// </summary>
public interface IPipelineBehavior<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    /// <summary>
    /// 包裹顺序。**大 = 外层，小 = 内层**（接近 handler）。
    /// 框架内置常量见 <see cref="TenE0.Core.Cqrs.Behaviors.BuiltInBehaviorOrders"/>。
    /// </summary>
    int Order => 0;

    Task<TResult> HandleAsync(
        TCommand command,
        CommandHandlerDelegate<TResult> next,
        CancellationToken cancellationToken);
}
