using TenE0.Core.Workflow.Definitions;

namespace TenE0.Api.Handlers;

/// <summary>
/// #158 演示：费用报销审批流程的 fluent API 定义。
///
/// 流程：start → 主管审批(Single) → 金额判断(>10000 走总监会签，否则直过) → 结束
/// 通过 AdminEndpoints 的 POST /admin/workflow/definitions 发布，或启动时 seeder 落库。
/// </summary>
public static class ExpenseClaimProcess
{
    public const string Code = "expense-claim";

    /// <summary>构建流程定义实体（未落库，由 seeder / endpoint 调用 PublishAsync 落库）。</summary>
    public static TenE0ProcessDefinition Build()
        => ProcessBuilder.Create(Code, "费用报销审批")
            .Category("finance")
            .Description("主管审批 → 金额>10000 走总监会签")
            .Start("start", "manager")
            .Approval("manager", "直属主管审批")
                .Assignee(AssigneePolicy.Manager())
                .Mode(ApprovalMode.Single)
                .Permission("expense.approve")
                .AllowRollback("manager")
                .Next("amount-check")
            .Branch("amount-check")
                .When("Amount", "gt", "10000", "director")
                .Default("end")
            .Approval("director", "财务总监会签")
                .Assignee(AssigneePolicy.Role("finance-director"))
                .Mode(ApprovalMode.Countersign)
                .Next("end")
            .End("end")
            .Build();
}
