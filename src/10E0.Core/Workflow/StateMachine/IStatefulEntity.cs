namespace TenE0.Core.Workflow.StateMachine;

/// <summary>
/// 标记接口 — 实体声明自己持有的状态类型。
///
/// 实现是<b>可选</b>的（状态机核心引擎 <see cref="StateMachine{TState,TAction}"/>
/// 不要求实体实现任何接口），但实现后可享受：
/// <list type="bullet">
/// <item>启动期 <c>IStateMachineRegistry</c> 可按实体类型自动发现并绑定对应状态机</item>
/// <item>约定一致性 — 阅读 <c>[StateMachine]</c> 标注的实体一眼能看出它的状态字段类型</item>
/// </list>
///
/// <b>当前无运行时行为</b>（预留用于未来的静态分析 / 自动绑定）。实现它纯粹是声明性约定。
///
/// 用法：
/// <code>
/// public sealed class Order : IStatefulEntity&lt;OrderState&gt;
/// {
///     public OrderState State { get; set; }
/// }
/// </code>
/// </summary>
/// <typeparam name="TState">状态类型（通常是 enum）。</typeparam>
public interface IStatefulEntity<TState> where TState : notnull
{
    /// <summary>实体当前状态。状态机转换后由业务方回写。</summary>
    TState State { get; set; }
}
