using TenE0.Core.Events;

namespace TenE0.Core.Workflow.StateMachine;

/// <summary>
/// 状态机核心引擎 — 判断 + 触发，不持久化。
///
/// 设计原则（issue #157）：
/// <list type="bullet">
/// <item><b>状态机不持久化</b>：实体自带 State 字段，状态机只负责"判断是否允许 + 触发事件"。</item>
/// <item><b>事件可选</b>：构造时注入 <see cref="IDomainEventDispatcher"/>（null 时不触发），
///   避免与 Outbox 强耦合，纯逻辑场景（如领域方法内调用）零副作用。</item>
/// <item><b>不可变定义</b>：运行时只读，多线程安全。</item>
/// </list>
///
/// 用法：
/// <code>
/// var (newState, transition) = await sm.FireAsync(entity.State, OrderAction.Submit, entity, "u001", ct);
/// entity.State = newState; // 业务方回写
/// </code>
/// </summary>
public sealed class StateMachine<TState, TAction>
    where TState : notnull where TAction : notnull
{
    private readonly StateMachineDefinition<TState, TAction> _definition;
    private readonly IDomainEventDispatcher? _eventDispatcher;

    internal StateMachine(
        StateMachineDefinition<TState, TAction> definition,
        IDomainEventDispatcher? eventDispatcher)
    {
        definition.EnsureFrozen();
        _definition = definition;
        _eventDispatcher = eventDispatcher;
    }

    public StateMachineDefinition<TState, TAction> Definition => _definition;

    /// <summary>
    /// 触发动作，执行状态转换。
    /// </summary>
    /// <param name="currentState">实体当前状态。</param>
    /// <param name="action">触发的动作。</param>
    /// <param name="entity">触发转换的实体（用于 Guard 求值 + 事件载荷）。无 Guard 时传 null 也可。</param>
    /// <param name="actor">触发者（用户 code / 系统标识）。</param>
    /// <param name="reason">备注。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(新状态, 转换记录)。失败抛 <see cref="InvalidTransitionException{TState,TAction}"/> 或 <see cref="GuardFailedException"/>。</returns>
    public async Task<(TState NewState, StateTransition<TState, TAction> Transition)> FireAsync(
        TState currentState,
        TAction action,
        object? entity,
        string actor,
        CancellationToken ct = default)
    {
        var target = ResolveTarget(currentState, action);

        await EvaluateGuardsAsync(currentState, action, entity, ct);

        var transition = new StateTransition<TState, TAction>
        {
            From = currentState,
            To = target,
            Action = action,
            Actor = actor,
        };

        await DispatchEventsAsync(entity, transition, ct);

        return (target, transition);
    }

    /// <summary>查询 (state, action) 是否是合法转换（不实际触发）。</summary>
    public bool CanFire(TState currentState, TAction action)
        => _definition.ActionTransitions.ContainsKey((currentState, action))
           || _definition.ActionTransitions.ContainsKey((default!, action)); // FromAny 注册

    private TState ResolveTarget(TState from, TAction action)
    {
        // 1. 精确 (from, action) 命中
        if (_definition.ActionTransitions.TryGetValue((from, action), out var target))
            return target;

        // 2. FromAny 注册（key 的 from 为 default!）
        if (_definition.ActionTransitions.TryGetValue((default!, action), out var anyTarget))
            return anyTarget;

        throw new InvalidTransitionException<TState, TAction>(from, action);
    }

    private async Task EvaluateGuardsAsync(TState from, TAction action, object? entity, CancellationToken ct)
    {
        // Guard 按 (From, Action) 查找；FromAny 注册的 Guard 键为 (default!, action)，
        // 命中条件是精确键缺失且该 action 是 FromAny 注册的。
        IReadOnlyList<IGuard>? guards = null;
        var isFromAny = _definition.FromAnyRegistered.Contains(action);
        if (isFromAny)
            _definition.Guards.TryGetValue((default!, action), out guards);
        else
            _definition.Guards.TryGetValue((from, action), out guards);

        if (guards is null || guards.Count == 0)
            return;

        var failed = new List<string>();
        foreach (var guard in guards)
        {
            // Guard 的 TEntity 类型可能不匹配 entity 实际类型；类型不符视为跳过（不阻止）。
            if (entity is null || !guard.CanHandle(entity.GetType()))
                continue;

            var ok = await guard.EvaluateAsync(entity, ct);
            if (!ok) failed.Add(guard.Reason);
        }

        if (failed.Count > 0)
            throw new GuardFailedException(failed);
    }

    private async Task DispatchEventsAsync(object? entity, StateTransition<TState, TAction> transition, CancellationToken ct)
    {
        if (_eventDispatcher is null || entity is null) return;

        // 三组事件：Exited → Transition → Entered（顺序模拟"先离开旧状态，发生转换，再进入新状态"）
        await _eventDispatcher.DispatchAsync(
            CreateEvent(() => new StateExitedEvent<object>(entity, transition.To, transition.Actor)), ct);

        await _eventDispatcher.DispatchAsync(
            new StateTransitionEvent<object, TState, TAction>(entity, transition), ct);

        await _eventDispatcher.DispatchAsync(
            CreateEvent(() => new StateEnteredEvent<object>(entity, transition.To, transition.Actor)), ct);
    }

    // helper：惰性创建事件（避免 entity 类型不匹配时的反射开销）
    private static IDomainEvent CreateEvent(Func<IDomainEvent> factory) => factory();
}

/// <summary>
/// 静态工厂入口 — 提供 fluent API 起点和带事件的引擎构造。
/// </summary>
public static class StateMachine
{
    /// <summary>创建 Fluent Builder，从指定初始状态开始定义。</summary>
    public static StateMachineBuilder<TState, TAction> Create<TState, TAction>(TState initialState)
        where TState : notnull where TAction : notnull
        => new(initialState);

    /// <summary>基于已构建的 <paramref name="definition"/> 创建引擎实例（无事件派发）。</summary>
    public static StateMachine<TState, TAction> Create<TState, TAction>(
        StateMachineDefinition<TState, TAction> definition)
        where TState : notnull where TAction : notnull
        => new(definition, eventDispatcher: null);

    /// <summary>基于已构建的 <paramref name="definition"/> 创建引擎实例（带事件派发）。</summary>
    public static StateMachine<TState, TAction> Create<TState, TAction>(
        StateMachineDefinition<TState, TAction> definition,
        IDomainEventDispatcher eventDispatcher)
        where TState : notnull where TAction : notnull
        => new(definition, eventDispatcher);
}
