using TenE0.Api.Domain;
using TenE0.Core.Workflow.StateMachine;

namespace TenE0.Api.Handlers;

/// <summary>
/// #157 演示：订单状态机的 fluent API 定义。
/// 通过 <c>AddTenE0WorkflowStateMachine</c> 扫描注册，运行时用 <c>IStateMachineRegistry.Get&lt;OrderState, OrderAction&gt;()</c> 获取。
/// </summary>
public sealed class OrderStateMachineDefinition : StateMachineDefinitionBase<OrderState, OrderAction>
{
    public override StateMachineDefinition<OrderState, OrderAction> Define()
        => StateMachine.Create<OrderState, OrderAction>(OrderState.Draft)
            .On(OrderAction.Submit).Transit(OrderState.Draft).To(OrderState.Submitted)
                .Guard<Order>(o => o.Items.Count > 0, "ORDER_NO_ITEMS")
                .And()
            .On(OrderAction.Approve).Transit(OrderState.Submitted).To(OrderState.Approved)
                .And()
            .On(OrderAction.Reject).Transit(OrderState.Submitted).To(OrderState.Rejected)
                .And()
            .On(OrderAction.Complete).Transit(OrderState.Approved).To(OrderState.Completed)
                .And()
            .On(OrderAction.Cancel).FromAny().To(OrderState.Cancelled)
                .Guard<Order>(o => o.State != OrderState.Completed, "ORDER_ALREADY_COMPLETED")
                .And()
            .Build();
}
