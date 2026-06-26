using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TenE0.Core.Abstractions;
using TenE0.Core.Observability;

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

    public async Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default)
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

        // #161 可观测性埋点：未注册 Observability 时 metrics == null → no-op，零热路径开销。
        // 用 GetService（而非构造注入）避免 CommandDispatcher 在未启用 Observability 时强依赖 TenE0Metrics。
        var metrics = serviceProvider.GetService<TenE0Metrics>();
        if (metrics is null)
            return await wrapper.HandleAsync(command, serviceProvider, cancellationToken);

        var start = Stopwatch.GetTimestamp();
        try
        {
            var result = await wrapper.HandleAsync(command, serviceProvider, cancellationToken);
            metrics.CommandTotal.Add(1, SuccessTags(commandType.Name));
            return result;
        }
        catch
        {
            metrics.CommandTotal.Add(1, FailureTags(commandType.Name));
            throw;
        }
        finally
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            metrics.CommandDuration.Record(elapsedMs, [new(TenE0Metrics.Tags.Command, commandType.Name)]);
        }
    }

    // 用集合表达式构造 KeyValuePair[] 显式匹配 params 重载，规避
    // Counter<long>.Add(long, KVP) 与 Add(long, params KVP[]) 单标签时的重载歧义。
    private static KeyValuePair<string, object?>[] SuccessTags(string commandName) =>
    [
        new(TenE0Metrics.Tags.Command, commandName),
        new(TenE0Metrics.Tags.Result, TenE0Metrics.Tags.Success),
    ];

    private static KeyValuePair<string, object?>[] FailureTags(string commandName) =>
    [
        new(TenE0Metrics.Tags.Command, commandName),
        new(TenE0Metrics.Tags.Result, TenE0Metrics.Tags.Failure),
    ];
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

        // #105: 用单个 BehaviorPipeline 对象替代链式闭包。
        // 旧实现每个 behavior 生成 1 个闭包（捕获 next + behavior + command）→ N 个 DisplayClass + N 个委托分配。
        // 新实现只 1 个 pipeline 对象 + 1 个指向 InvokeNextAsync 的委托（复用），behaviors 存数组按索引推进。
        // 单次 SendAsync 内串行调用，pipeline 对象不跨线程，状态机安全。
        if (behaviors.Length == 0)
            return handler.HandleAsync(typedCommand, cancellationToken);

        var pipeline = new BehaviorPipeline<TCommand, TResult>(behaviors, handler, typedCommand);
        return pipeline.InvokeNextAsync(0, cancellationToken);
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

/// <summary>
/// #105: 行为管道执行器 —— 用数组 + 索引推进替代链式闭包。
///
/// 旧实现每次 SendAsync 构建 N 个嵌套闭包（每个捕获 next + behavior + command），
/// 产生 N 个 DisplayClass 实例 + N 个委托分配。本类把 behaviors 存数组，按索引推进，
/// 整条管道只需 1 个 BehaviorPipeline 实例 + 每个 behavior 1 个轻量 index 闭包。
///
/// 调用语义：InvokeNextAsync(0) 调最外层 behavior（behaviors 已按 Order 降序排列，
/// index 0 = Order 最大 = 最外层），它调 next → InvokeNextAsync(1) → ... 直到最后调 handler。
/// </summary>
internal sealed class BehaviorPipeline<TCommand, TResult> where TCommand : ICommand<TResult>
{
    private readonly IPipelineBehavior<TCommand, TResult>[] _behaviors;
    private readonly ICommandHandler<TCommand, TResult> _handler;
    private readonly TCommand _command;

    internal BehaviorPipeline(
        IPipelineBehavior<TCommand, TResult>[] behaviors,
        ICommandHandler<TCommand, TResult> handler,
        TCommand command)
    {
        _behaviors = behaviors;
        _handler = handler;
        _command = command;
    }

    /// <summary>
    /// 执行索引 <paramref name="index"/> 处的 behavior；越界则调 handler（管道末端）。
    /// 每个 behavior 的 next 委托指向 InvokeNextAsync(index+1)，复用本实例。
    /// </summary>
    internal Task<TResult> InvokeNextAsync(int index, CancellationToken cancellationToken)
    {
        if (index >= _behaviors.Length)
            return _handler.HandleAsync(_command, cancellationToken);

        // next 委托：指向本实例的 InvokeNextAsync(index+1)，只捕获 index（int）+ this。
        // 比 旧行为捕获整个 next 链 + behavior + command 的 DisplayClass 更轻。
        CommandHandlerDelegate<TResult> next = ct => InvokeNextAsync(index + 1, ct);
        return _behaviors[index].HandleAsync(_command, next, cancellationToken);
    }
}
