namespace TenE0.Core.Workflow.StateMachine;

/// <summary>
/// 业务方实现此接口声明一个状态机定义。
///
/// 实现类被 <see cref="IStateMachineRegistry"/> 启动期扫描注册，业务代码通过
/// <c>registry.Get&lt;TState, TAction&gt;()</c> 获取引擎实例。
///
/// 用法：
/// <code>
/// public sealed class OrderStateMachineDefinition
///     : IStateMachineDefinition&lt;OrderState, OrderAction&gt;
/// {
///     public StateMachineDefinition&lt;OrderState, OrderAction&gt; Define()
///         => StateMachine&lt;OrderState, OrderAction&gt;.Create(OrderState.Draft)
///             .On(OrderAction.Submit).Transit(OrderState.Draft).To(OrderState.Submitted)
///             .And()
///             .Build();
/// }
/// </code>
/// </summary>
public interface IStateMachineDefinition<TState, TAction>
    where TState : notnull where TAction : notnull
{
    /// <summary>构建并返回（冻结后的）状态机定义。</summary>
    StateMachineDefinition<TState, TAction> Define();
}

/// <summary>
/// 类型擦除的基接口，便于 DI 容器按非泛型 <see cref="IStateMachineDefinition"/> 注册并枚举。
/// 业务方实现泛型版 <see cref="IStateMachineDefinition{TState,TAction}"/> 即可。
/// </summary>
public interface IStateMachineDefinition
{
    /// <summary>状态类型（用于 Registry 的 (TState,TAction) 键）。</summary>
    Type StateType { get; }

    /// <summary>动作类型。</summary>
    Type ActionType { get; }

    /// <summary>已构建的定义（类型擦除，调用方按需强转）。</summary>
    object Build();
}

/// <summary>
/// 非泛型适配基类 — 业务方继承 <see cref="StateMachineDefinitionBase{TState,TAction}"/>
/// 即同时满足泛型和非泛型契约，免去重复样板。
/// </summary>
public abstract class StateMachineDefinitionBase<TState, TAction> :
    IStateMachineDefinition, IStateMachineDefinition<TState, TAction>
    where TState : notnull where TAction : notnull
{
    public Type StateType => typeof(TState);
    public Type ActionType => typeof(TAction);

    public abstract StateMachineDefinition<TState, TAction> Define();

    public object Build()
    {
        var def = Define();
        def.Freeze();
        return def;
    }
}
