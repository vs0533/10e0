using TenE0.Core.Workflow.StateMachine;

namespace TenE0.Api.Domain;

/// <summary>
/// #157 演示：订单状态机（枚举状态 + 枚举动作）。
/// 与字符串状态相比，泛型状态机在编译期捕获 typo。
/// </summary>
public enum OrderState
{
    Draft = 0,
    Submitted = 1,
    Approved = 2,
    Rejected = 3,
    Cancelled = 4,
    Completed = 5,
}

public enum OrderAction
{
    Submit,
    Approve,
    Reject,
    Cancel,
    Complete,
}

/// <summary>
/// 演示实体 — 用 <c>[StateMachine]</c> 标注声明其状态机，并实现 <see cref="IStatefulEntity{TState}"/>。
/// 注意：状态机引擎本身不要求实体实现任何接口，这里仅为约定展示。
/// </summary>
[StateMachine(typeof(OrderState), typeof(OrderAction))]
public sealed class Order : IStatefulEntity<OrderState>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Code { get; set; } = "";
    public OrderState State { get; set; } = OrderState.Draft;
    public List<string> Items { get; set; } = [];
    public decimal Amount { get; set; }
}
