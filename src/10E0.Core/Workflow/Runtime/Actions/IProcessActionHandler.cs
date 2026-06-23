using TenE0.Core.Workflow.Definitions;

namespace TenE0.Core.Workflow.Runtime;

/// <summary>
/// 审批操作处理器 — 按 <see cref="ProcessActionKind"/> 分发。
/// 每个 handler 负责一种操作的语义实现（Approve/Reject/Delegate/AddSigner/Rollback）。
///
/// handler 内部通过 <see cref="IWorkflowEngine"/> 推进节点（节点判定通过后调 engine.AdvanceAsync）。
/// </summary>
public interface IProcessActionHandler
{
    /// <summary>本 handler 处理的操作种类。</summary>
    ProcessActionKind ActionKind { get; }

    /// <summary>执行操作。</summary>
    Task<ProcessActionResult> ExecuteAsync(
        TenE0ProcessInstance instance,
        ExecuteActionRequest req,
        IProcessNode currentNode,
        CancellationToken ct = default);
}

/// <summary>
/// 流程引擎核心 — 节点推进、审批人解析、判定通过。
/// 由 ProcessRuntimeService 和 ActionHandler 共享。
/// </summary>
public interface IWorkflowEngine
{
    /// <summary>
    /// 启动流程后创建首节点任务（解析首个审批节点的审批人 + 创建 Task）。
    /// </summary>
    Task<IReadOnlyList<string>> CreateTasksForNodeAsync(
        TenE0ProcessInstance instance,
        IProcessNode node,
        ResolveContext resolveCtx,
        CancellationToken ct = default);

    /// <summary>
    /// 判定当前节点是否通过（按 ApprovalMode），通过则推进到下一节点并创建新任务。
    /// 返回推进结果（下一节点 + 新任务审批人）。已是 End 节点则完成实例。
    /// </summary>
    Task<NodeAdvanceResult> TryAdvanceAsync(
        TenE0ProcessInstance instance,
        IProcessNode currentNode,
        ResolveContext resolveCtx,
        string actor,
        string? comment,
        CancellationToken ct = default);

    /// <summary>解析下一节点（处理 Branch 条件路由 / End 完成）。</summary>
    Task<IProcessNode?> ResolveNextNodeAsync(
        TenE0ProcessInstance instance,
        IProcessNode currentNode,
        ResolveContext resolveCtx,
        CancellationToken ct = default);

    /// <summary>
    /// 从指定节点开始，穿越路由节点（Start/Branch），停在首个需创建任务的 Approval/Parallel 节点，
    /// 为其创建任务并返回。若中途遇到 End 则返回 completed=true。
    /// 用于流程启动（Start→...→首个审批节点）。
    /// </summary>
    Task<NodeAdvanceResult> AdvanceToFirstTaskNodeAsync(
        TenE0ProcessInstance instance,
        IProcessNode fromNode,
        ResolveContext resolveCtx,
        CancellationToken ct = default);
}

/// <summary>节点推进结果。</summary>
public sealed record NodeAdvanceResult(
    bool Advanced,
    string? NextNodeCode,
    IReadOnlyList<string> NewTaskAssignees,
    ProcessStatus? InstanceFinalStatus);
