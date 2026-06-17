using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
/// #41 增量：
/// - Behaviors 按 <see cref="IPipelineBehavior{TCommand, TResult}.Order"/> 升序排序后再逆序包裹
///   （同 Order 用稳定排序，按注册顺序作为 tiebreaker → 保持旧"先注册 = 外层"约定）
/// - Behaviors 通过 <see cref="BehaviorOptions.DisabledInTest"/> 列表 + <see cref="SkipBehaviorInTestEnvAttribute"/>
///   跳过指定作用域的执行
///
/// Wrapper 模式参考自 MediatR 的设计，但实现完全独立，无许可证依赖。
/// </summary>
internal sealed class CommandDispatcher(IServiceProvider serviceProvider) : ICommandDispatcher
{
    /// <summary>
    /// 静态缓存：同一命令类型在进程级只构造一次 wrapper 实例。
    /// </summary>
    /// <remarks>
    /// 标注为 <c>internal</c> 而非 <c>private</c> 仅供 10E0.Core.Tests 反射-free 验证缓存行为。
    /// 不要在生产代码里读/写这个字段——它是 dispatcher 内部实现细节。
    /// </remarks>
    internal static readonly ConcurrentDictionary<Type, object> WrapperCache = new();

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

        // 取出所有管道行为；按 Order 升序排序（Order 小 = 外层），并跳过 DisabledInTest + SkipBehaviorInTestEnv 命中的。
        // 同 Order 用稳定排序保持注册顺序作为 tiebreaker。
        var raw = serviceProvider.GetServices<IPipelineBehavior<TCommand, TResult>>();
        var behaviors = FilterAndSortBehaviors(serviceProvider, raw);

        // 起点：调用真正的 handler
        CommandHandlerDelegate<TResult> pipeline = ct => handler.HandleAsync(typedCommand, ct);

        // 逆序包裹：保证 Order 小的行为最先进入、最后退出（与 ASP.NET Core 中间件一致）
        foreach (var behavior in behaviors.Reverse())
        {
            var next = pipeline;
            pipeline = ct => behavior.HandleAsync(typedCommand, next, ct);
        }

        return pipeline(cancellationToken);
    }

    /// <summary>
    /// #41: 过滤 + 排序 behaviors。
    ///
    /// 过滤规则（按顺序应用，命中任一即跳过）：
    /// 1. <see cref="SkipBehaviorInTestEnvAttribute"/> + Scope 匹配 <see cref="BehaviorOptions.Environment"/>
    /// 2. <see cref="BehaviorOptions.DisabledInTest"/> 列表包含此 behavior 的具体类型全名
    ///
    /// 排序规则：<see cref="IPipelineBehavior{TCommand, TResult}.Order"/> **降序**（大 = 外层）；
    /// 同 Order 保持注册顺序作 tiebreaker。约定来自 issue #41 的示例：
    /// Logging(200) > Transaction(100) > Permission(50) → Logging 最外层。
    /// </summary>
    private static IPipelineBehavior<TCommand, TResult>[] FilterAndSortBehaviors(
        IServiceProvider serviceProvider,
        IEnumerable<IPipelineBehavior<TCommand, TResult>> raw)
    {
        // 解析 BehaviorOptions；未注册时降级为默认（Production 模式，所有 behavior 都跑）
        BehaviorOptions options;
        try
        {
            options = serviceProvider.GetService<IOptions<BehaviorOptions>>()?.Value ?? new BehaviorOptions();
        }
        catch (Exception)
        {
            // OptionsMonitor / OptionsManager 在某些极简测试 ServiceProvider 下可能未注册 — 失败回退到默认
            options = new BehaviorOptions();
        }

        var env = options.Environment ?? "Production";

        // 缓存 attribute 反射结果（按具体类型）— 每个 behavior 类型只查一次
        var attrCache = new Dictionary<Type, SkipBehaviorInTestEnvAttribute?>();

        var filtered = new List<IPipelineBehavior<TCommand, TResult>>();
        foreach (var b in raw)
        {
            var concreteType = b.GetType();

            // Rule 1: [SkipBehaviorInTestEnv] 标注
            if (!attrCache.TryGetValue(concreteType, out var skipAttr))
            {
                skipAttr = concreteType.GetCustomAttribute<SkipBehaviorInTestEnvAttribute>(inherit: true);
                attrCache[concreteType] = skipAttr;
            }

            if (skipAttr is not null && ShouldSkipByAttribute(skipAttr.Scope, env))
                continue;

            // Rule 2: DisabledInTest 列表
            if (env == "Test" && options.DisabledInTest.Count > 0)
            {
                var typeName = concreteType.FullName ?? string.Empty;
                if (options.DisabledInTest.Contains(typeName))
                    continue;
            }

            filtered.Add(b);
        }

        // 稳定排序：Order 降序（大 = 外层），同 Order 保持原有顺序（注册顺序）。
        // Enumerable.OrderBy/OrderByDescending 在 LINQ to Objects 上是稳定排序（.NET 6+）。
        return filtered
            .Select((b, i) => (Behavior: b, Index: i, Order: b.Order))
            .OrderByDescending(t => t.Order)
            .ThenBy(t => t.Index)
            .Select(t => t.Behavior)
            .ToArray();
    }

    private static bool ShouldSkipByAttribute(string? scope, string env)
    {
        if (string.IsNullOrEmpty(scope)) return false;
        if (scope.Equals("All", StringComparison.OrdinalIgnoreCase)) return true;
        return scope.Equals(env, StringComparison.OrdinalIgnoreCase);
    }
}
