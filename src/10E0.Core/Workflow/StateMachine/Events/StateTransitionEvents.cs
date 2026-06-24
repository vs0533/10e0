using TenE0.Core.Events;

namespace TenE0.Core.Workflow.StateMachine;

/// <summary>
/// 状态机转换事件契约（领域事件风格，复用 <see cref="IDomainEventDispatcher"/>）。
///
/// 三组事件按"离开旧状态 → 发生转换 → 进入新状态"语义顺序派发。
/// 业务方可订阅 <see cref="StateTransitionEvent{TEntity,TState,TAction}"/>
/// 做副作用：通知（#155）、审计（#152）、流程驱动（#159）。
///
/// 设计权衡：事件用 object 持有实体（不强制泛型 T），避免"为每个实体类型各注册一套事件"
/// 的爆炸；订阅者按需在 handler 内强转。对强类型有要求的场景，业务方可定义自己的
/// 派生事件并通过 Guard / 包装触发。
/// </summary>

/// <summary>实体进入某状态。</summary>
public sealed record StateEnteredEvent<TEntity>(TEntity Entity, object State, string Actor) : IDomainEvent;

/// <summary>实体离开某状态。</summary>
public sealed record StateExitedEvent<TEntity>(TEntity Entity, object State, string Actor) : IDomainEvent;

/// <summary>状态转换发生（携带完整 <see cref="StateTransition{TState,TAction}"/>）。</summary>
public sealed record StateTransitionEvent<TEntity, TState, TAction>(
    TEntity Entity,
    StateTransition<TState, TAction> Transition) : IDomainEvent
    where TState : notnull where TAction : notnull;
