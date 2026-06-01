using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.Abstractions;

namespace TenE0.Core.Cqrs;

/// <summary>
/// ICommandDispatcher 默认实现。
///
/// 设计：
/// - Scoped 生命周期 — 接收请求作用域的 IServiceProvider，可解析 Scoped Handler
/// - Wrapper 模式 — 每个命令类型一次性构造 wrapper 实例并缓存到静态字典
/// - 零热路径反射 — 缓存命中后直接调用泛型 wrapper
///
/// Wrapper 模式参考自 MediatR 的设计，但实现完全独立，无许可证依赖。
/// </summary>
internal sealed class CommandDispatcher(IServiceProvider serviceProvider) : ICommandDispatcher
{
    // 静态缓存：同一命令类型在进程级只构造一次 wrapper 实例
    private static readonly ConcurrentDictionary<Type, object> WrapperCache = new();

    public Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var commandType = command.GetType();

        // 缓存键是命令具体类型；TResult 由 ICommand<TResult> 约束推导
        var wrapper = (CommandHandlerWrapperBase<TResult>)WrapperCache.GetOrAdd(
            commandType,
            static (type, resultType) =>
            {
                var wrapperType = typeof(CommandHandlerWrapper<,>).MakeGenericType(type, resultType);
                return Activator.CreateInstance(wrapperType)!;
            },
            typeof(TResult));

        return wrapper.HandleAsync(command, serviceProvider, cancellationToken);
    }
}

/// <summary>
/// Wrapper 基类，让 CommandDispatcher 能以非泛型方式持有缓存。
/// </summary>
internal abstract class CommandHandlerWrapperBase<TResult>
{
    public abstract Task<TResult> HandleAsync(object command, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

/// <summary>
/// 具体类型的 wrapper：每个命令类型生成一个具体类型实例，调用强类型 Handler 和 Behaviors。
/// </summary>
internal sealed class CommandHandlerWrapper<TCommand, TResult> : CommandHandlerWrapperBase<TResult>
    where TCommand : ICommand<TResult>
{
    public override Task<TResult> HandleAsync(object command, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var typedCommand = (TCommand)command;
        var handler = serviceProvider.GetRequiredService<ICommandHandler<TCommand, TResult>>();

        // 取出所有管道行为；反向遍历，让先注册的行为在外层（与 ASP.NET Core 中间件一致）
        var behaviors = serviceProvider.GetServices<IPipelineBehavior<TCommand, TResult>>();

        // 起点：调用真正的 handler
        CommandHandlerDelegate<TResult> pipeline = ct => handler.HandleAsync(typedCommand, ct);

        // 逆序包裹：保证先注册的 behavior 最先进入、最后退出
        foreach (var behavior in behaviors.Reverse())
        {
            var next = pipeline;
            pipeline = ct => behavior.HandleAsync(typedCommand, next, ct);
        }

        return pipeline(cancellationToken);
    }
}
