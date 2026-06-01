namespace TenE0.Core.Abstractions;

/// <summary>
/// 命令分发器（替代 MediatR.IMediator）。
///
/// 关键差异：
/// - 单一职责：只做 Send（命令分发），不掺杂 Publish（事件广播）
/// - 不依赖 MediatR（许可证 + 升级风险）
/// - 实现内置 wrapper 缓存，零反射热路径开销（首次分发后路径固定）
///
/// 批量命令的事务包裹由 <c>ITransactional</c> 管道行为统一接管，
/// 不再像旧 CommandManager.Dispatch 那样在分发器里写嵌套事务循环（修复 BUG-001）。
/// </summary>
public interface ICommandDispatcher
{
    /// <summary>分发命令到对应的 ICommandHandler，结果由管道层层返回。</summary>
    Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default);
}
