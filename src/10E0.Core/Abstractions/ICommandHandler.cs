namespace TenE0.Core.Abstractions;

/// <summary>
/// 命令处理器。
///
/// 与旧 BaseHandler 的差异：
/// - 不再耦合 E0Context — 通过构造函数注入需要的依赖
/// - 不再有 200+ 行的通用 CRUD 流程基类（CRUD 流程归 EntityServer 模块）
/// - 接口纯粹，只有一个 HandleAsync 方法
/// </summary>
public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken);
}
