using Microsoft.EntityFrameworkCore;
using TenE0.Core.Workflow.Definitions;

namespace TenE0.Core.Workflow.Runtime;

/// <summary>Approve 操作：标记 Task 完成，按 ApprovalMode 判定节点是否通过，通过则推进。</summary>
public sealed class ApproveActionHandler<TContext>(
    IDbContextFactory<TContext> contextFactory,
    IWorkflowEngine engine,
    TimeProvider timeProvider) : IProcessActionHandler
    where TContext : DbContext
{
    public ProcessActionKind ActionKind => ProcessActionKind.Approve;

    public async Task<ProcessActionResult> ExecuteAsync(
        TenE0ProcessInstance instance,
        ExecuteActionRequest req,
        IProcessNode currentNode,
        CancellationToken ct = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);

        // 找到该审批人在当前节点的 Pending Task
        var task = await dc.Set<TenE0ProcessTask>()
            .Where(t => t.InstanceId == instance.Id
                && t.NodeCode == currentNode.Code
                && t.Assignee == req.Actor
                && t.Status == ProcessTaskStatus.Pending
                && !t.IsSoftDelete)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(
                $"用户 '{req.Actor}' 在节点 '{currentNode.Code}' 没有待处理任务，无法审批。");

        task.Status = ProcessTaskStatus.Approved;
        task.CompletedAt = timeProvider.GetUtcNow();
        task.CompletedBy = req.Actor;
        task.Comment = req.Comment;

        await dc.SaveChangesAsync(ct);

        // 写历史
        await WriteHistoryAsync(dc, instance.Id, currentNode.Code, "Approve", req.Actor, null, req.Comment, ct);

        // 判定推进
        var resolveCtx = await BuildResolveContextAsync(dc, instance, ct);
        var result = await engine.TryAdvanceAsync(instance, currentNode, resolveCtx, req.Actor, req.Comment, ct);

        // 实例完成
        if (result.InstanceFinalStatus is not null)
        {
            await MarkInstanceCompletedAsync(dc, instance.Id, result.InstanceFinalStatus.Value, ct);
        }

        await dc.SaveChangesAsync(ct);
        return new ProcessActionResult(instance.Id, result.InstanceFinalStatus ?? instance.Status,
            result.NextNodeCode, result.NewTaskAssignees);
    }

    // 共享辅助（避免每个 handler 重复）
    internal static async Task WriteHistoryAsync(
        DbContext dc, string instanceId, string nodeCode, string action,
        string actor, string? assignee, string? comment, CancellationToken ct)
    {
        dc.Set<TenE0ProcessHistory>().Add(new TenE0ProcessHistory
        {
            InstanceId = instanceId,
            NodeCode = nodeCode,
            Action = action,
            Actor = actor,
            Assignee = assignee,
            Comment = comment,
            Timestamp = DateTimeOffset.UtcNow,
        });
        await Task.CompletedTask;
    }

    internal static async Task<ResolveContext> BuildResolveContextAsync(
        DbContext dc, TenE0ProcessInstance instance, CancellationToken ct)
    {
        // 业务数据从 SummaryJson 反序列化（轻量：本期 SummaryJson 即业务数据）
        Dictionary<string, object?> data = [];
        if (!string.IsNullOrEmpty(instance.SummaryJson))
        {
            try
            {
                data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(instance.SummaryJson) ?? [];
            }
            catch { /* 解析失败用空字典 */ }
        }
        await Task.CompletedTask;
        return new ResolveContext
        {
            Initiator = instance.Initiator,
            InitiatorOrgId = instance.InitiatorOrgId,
            TenantId = instance.TenantId,
            BusinessData = data,
        };
    }

    internal static async Task MarkInstanceCompletedAsync(
        DbContext dc, string instanceId, ProcessStatus status, CancellationToken ct)
    {
        var inst = await dc.Set<TenE0ProcessInstance>().FirstAsync(i => i.Id == instanceId, ct);
        inst.Status = status;
        inst.CompletedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>Reject 操作：标记 Task 拒绝，节点终止，实例置 Rejected。</summary>
public sealed class RejectActionHandler<TContext>(
    IDbContextFactory<TContext> contextFactory,
    TimeProvider timeProvider) : IProcessActionHandler
    where TContext : DbContext
{
    public ProcessActionKind ActionKind => ProcessActionKind.Reject;

    public async Task<ProcessActionResult> ExecuteAsync(
        TenE0ProcessInstance instance,
        ExecuteActionRequest req,
        IProcessNode currentNode,
        CancellationToken ct = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);

        var task = await dc.Set<TenE0ProcessTask>()
            .Where(t => t.InstanceId == instance.Id
                && t.NodeCode == currentNode.Code
                && t.Assignee == req.Actor
                && t.Status == ProcessTaskStatus.Pending
                && !t.IsSoftDelete)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(
                $"用户 '{req.Actor}' 在节点 '{currentNode.Code}' 没有待处理任务，无法驳回。");

        task.Status = ProcessTaskStatus.Rejected;
        task.CompletedAt = timeProvider.GetUtcNow();
        task.CompletedBy = req.Actor;
        task.Comment = req.Comment;

        // 同节点其他 Pending Task 作废
        var siblings = await dc.Set<TenE0ProcessTask>()
            .Where(t => t.InstanceId == instance.Id
                && t.NodeCode == currentNode.Code
                && t.Status == ProcessTaskStatus.Pending
                && t.Id != task.Id)
            .ToListAsync(ct);
        foreach (var s in siblings) s.Status = ProcessTaskStatus.Voided;

        await ApproveActionHandler<TContext>.WriteHistoryAsync(dc, instance.Id, currentNode.Code, "Reject", req.Actor, null, req.Comment, ct);

        // 实例终止
        var inst = await dc.Set<TenE0ProcessInstance>().FirstAsync(i => i.Id == instance.Id, ct);
        inst.Status = ProcessStatus.Rejected;
        inst.CompletedAt = timeProvider.GetUtcNow();

        await dc.SaveChangesAsync(ct);
        return new ProcessActionResult(instance.Id, ProcessStatus.Rejected, null, []);
    }
}

/// <summary>Delegate 操作：当前 Task 标记 Delegated，为被委派人新建 Task（同节点）。</summary>
public sealed class DelegateActionHandler<TContext>(
    IDbContextFactory<TContext> contextFactory,
    TimeProvider timeProvider) : IProcessActionHandler
    where TContext : DbContext
{
    public ProcessActionKind ActionKind => ProcessActionKind.Delegate;

    public async Task<ProcessActionResult> ExecuteAsync(
        TenE0ProcessInstance instance,
        ExecuteActionRequest req,
        IProcessNode currentNode,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.DelegateTo))
            throw new InvalidOperationException("Delegate 操作必须提供 DelegateTo");

        if (currentNode is not ApprovalNode an || !an.AllowDelegate)
            throw new InvalidOperationException($"节点 '{currentNode.Code}' 不允许委派（AllowDelegate=false）");

        await using var dc = await contextFactory.CreateDbContextAsync(ct);

        var task = await dc.Set<TenE0ProcessTask>()
            .Where(t => t.InstanceId == instance.Id
                && t.NodeCode == currentNode.Code
                && t.Assignee == req.Actor
                && t.Status == ProcessTaskStatus.Pending
                && !t.IsSoftDelete)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(
                $"用户 '{req.Actor}' 在节点 '{currentNode.Code}' 没有待处理任务，无法委派。");

        task.Status = ProcessTaskStatus.Delegated;
        task.CompletedAt = timeProvider.GetUtcNow();
        task.CompletedBy = req.Actor;
        task.Comment = req.Comment;

        // 为被委派人新建 Task
        var now = timeProvider.GetUtcNow();
        dc.Set<TenE0ProcessTask>().Add(new TenE0ProcessTask
        {
            InstanceId = instance.Id,
            NodeCode = currentNode.Code,
            Assignee = req.DelegateTo,
            DelegatedBy = req.Actor,
            Status = ProcessTaskStatus.Pending,
            Deadline = an.Timeout is null ? null : now + an.Timeout,
        });

        await ApproveActionHandler<TContext>.WriteHistoryAsync(
            dc, instance.Id, currentNode.Code, "Delegate", req.Actor, req.DelegateTo, req.Comment, ct);

        await dc.SaveChangesAsync(ct);
        return new ProcessActionResult(instance.Id, instance.Status, currentNode.Code, [req.DelegateTo]);
    }
}

/// <summary>AddSigner 操作：在当前节点追加审批人 Task（会签场景临时加人）。</summary>
public sealed class AddSignerActionHandler<TContext>(
    IDbContextFactory<TContext> contextFactory,
    TimeProvider timeProvider) : IProcessActionHandler
    where TContext : DbContext
{
    public ProcessActionKind ActionKind => ProcessActionKind.AddSigner;

    public async Task<ProcessActionResult> ExecuteAsync(
        TenE0ProcessInstance instance,
        ExecuteActionRequest req,
        IProcessNode currentNode,
        CancellationToken ct = default)
    {
        if (req.AddSigners is null || req.AddSigners.Count == 0)
            throw new InvalidOperationException("AddSigner 操作必须提供 AddSigners 列表");

        if (currentNode is not ApprovalNode an || !an.AllowAddSigner)
            throw new InvalidOperationException($"节点 '{currentNode.Code}' 不允许加签（AllowAddSigner=false）");

        await using var dc = await contextFactory.CreateDbContextAsync(ct);

        // 校验操作者有权限（是当前节点 Pending Task 的审批人）
        var operatorTask = await dc.Set<TenE0ProcessTask>()
            .AnyAsync(t => t.InstanceId == instance.Id
                && t.NodeCode == currentNode.Code
                && t.Assignee == req.Actor
                && t.Status == ProcessTaskStatus.Pending, ct);
        if (!operatorTask)
            throw new InvalidOperationException(
                $"用户 '{req.Actor}' 不是节点 '{currentNode.Code}' 的待办审批人，无法加签。");

        var now = timeProvider.GetUtcNow();
        foreach (var signer in req.AddSigners)
        {
            dc.Set<TenE0ProcessTask>().Add(new TenE0ProcessTask
            {
                InstanceId = instance.Id,
                NodeCode = currentNode.Code,
                Assignee = signer,
                Status = ProcessTaskStatus.Pending,
                Deadline = an.Timeout is null ? null : now + an.Timeout,
            });
        }

        await ApproveActionHandler<TContext>.WriteHistoryAsync(
            dc, instance.Id, currentNode.Code, "AddSigner", req.Actor,
            string.Join(",", req.AddSigners), req.Comment, ct);

        await dc.SaveChangesAsync(ct);
        return new ProcessActionResult(instance.Id, instance.Status, currentNode.Code, req.AddSigners);
    }
}

/// <summary>Rollback 操作：当前节点终止，回退到指定历史节点，重新生成该节点 Task。</summary>
public sealed class RollbackActionHandler<TContext>(
    IDbContextFactory<TContext> contextFactory,
    IWorkflowEngine engine) : IProcessActionHandler
    where TContext : DbContext
{
    public ProcessActionKind ActionKind => ProcessActionKind.Rollback;

    public async Task<ProcessActionResult> ExecuteAsync(
        TenE0ProcessInstance instance,
        ExecuteActionRequest req,
        IProcessNode currentNode,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.RollbackToNodeCode))
            throw new InvalidOperationException("Rollback 操作必须提供 RollbackToNodeCode");

        if (currentNode is not ApprovalNode an || !an.AllowRollback)
            throw new InvalidOperationException($"节点 '{currentNode.Code}' 不允许回退（AllowRollback=false）");

        if (!string.Equals(an.RollbackTargetCode, req.RollbackToNodeCode, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"节点 '{currentNode.Code}' 仅允许回退到 '{an.RollbackTargetCode}'，请求回退到 '{req.RollbackToNodeCode}' 被拒绝");

        await using var dc = await contextFactory.CreateDbContextAsync(ct);

        // 校验操作者权限
        var operatorTask = await dc.Set<TenE0ProcessTask>()
            .AnyAsync(t => t.InstanceId == instance.Id
                && t.NodeCode == currentNode.Code
                && t.Assignee == req.Actor
                && t.Status == ProcessTaskStatus.Pending, ct);
        if (!operatorTask)
            throw new InvalidOperationException(
                $"用户 '{req.Actor}' 不是节点 '{currentNode.Code}' 的待办审批人，无法回退。");

        // 当前节点所有 Pending Task 作废
        var pending = await dc.Set<TenE0ProcessTask>()
            .Where(t => t.InstanceId == instance.Id
                && t.NodeCode == currentNode.Code
                && t.Status == ProcessTaskStatus.Pending)
            .ToListAsync(ct);
        foreach (var t in pending) t.Status = ProcessTaskStatus.Voided;

        // 实例回退到目标节点
        var inst = await dc.Set<TenE0ProcessInstance>().FirstAsync(i => i.Id == instance.Id, ct);
        inst.CurrentNodeCode = req.RollbackToNodeCode;

        await ApproveActionHandler<TContext>.WriteHistoryAsync(
            dc, instance.Id, currentNode.Code, "Rollback", req.Actor, req.RollbackToNodeCode, req.Comment, ct);

        await dc.SaveChangesAsync(ct);

        // 为目标节点重新创建任务
        var def = await dc.Set<TenE0ProcessDefinition>()
            .FirstAsync(d => d.Id == instance.DefinitionId, ct);
        var nodes = ProcessNodeSerializer.DeserializeNodes(def.NodesJson);
        var targetNode = nodes.First(n => n.Code == req.RollbackToNodeCode);
        var resolveCtx = await ApproveActionHandler<TContext>.BuildResolveContextAsync(dc, instance, ct);
        var newAssignees = await engine.CreateTasksForNodeAsync(instance, targetNode, resolveCtx, ct);

        return new ProcessActionResult(instance.Id, instance.Status, req.RollbackToNodeCode, newAssignees);
    }
}
