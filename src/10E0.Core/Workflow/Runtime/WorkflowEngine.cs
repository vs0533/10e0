using Microsoft.EntityFrameworkCore;
using TenE0.Core.Events;
using TenE0.Core.Workflow.Definitions;

namespace TenE0.Core.Workflow.Runtime;

/// <summary>
/// 流程引擎核心实现 — 节点推进、审批人解析、判定通过。
///
/// 推进逻辑（TryAdvanceAsync）：
/// <list type="number">
/// <item>解析当前节点的所有 Task</item>
/// <item>按 ApprovalMode 判定是否通过：
///   Single/OrSign 任一 Approve 即通过；Countersign 全部 Approve 才通过；任一 Rejected 即终止。</item>
/// <item>通过后：解析下一节点（Branch 走条件路由）、创建新 Task、触发事件</item>
/// <item>下一节点是 End → 实例完成（Approved）</item>
/// </list>
///
/// 并发：依赖 Instance/Task 的 RowVersion 乐观锁（EF 抛 DbUpdateConcurrencyException 时上层重试）。
/// </summary>
public sealed class WorkflowEngine<TContext>(
    IDbContextFactory<TContext> contextFactory,
    IEnumerable<IAssigneeResolver> resolvers,
    IDomainEventDispatcher? eventDispatcher,
    TimeProvider timeProvider) : IWorkflowEngine
    where TContext : DbContext
{
    /// <summary>审批人解析器（按 AssigneePolicyKind 索引）。</summary>
    private readonly Dictionary<AssigneePolicyKind, IAssigneeResolver> _resolvers =
        resolvers.ToDictionary(r => r.PolicyName);

    public async Task<IReadOnlyList<string>> CreateTasksForNodeAsync(
        TenE0ProcessInstance instance,
        IProcessNode node,
        ResolveContext resolveCtx,
        CancellationToken ct = default)
    {
        var assignees = await ResolveAssigneesAsync(node, resolveCtx, ct);
        if (assignees.Count == 0)
            throw new InvalidOperationException(
                $"节点 '{node.Code}' 解析出的审批人为空，无法创建任务。检查 AssigneePolicy 配置。");

        await using var dc = await contextFactory.CreateDbContextAsync(ct);

        var now = timeProvider.GetUtcNow();
        var timeout = (node as ApprovalNode)?.Timeout;

        foreach (var assignee in assignees)
        {
            dc.Set<TenE0ProcessTask>().Add(new TenE0ProcessTask
            {
                InstanceId = instance.Id,
                NodeCode = node.Code,
                Assignee = assignee,
                Status = ProcessTaskStatus.Pending,
                Deadline = timeout is null ? null : now + timeout,
            });
        }

        await dc.SaveChangesAsync(ct);

        // 触发节点进入事件（供 #155 推送订阅）
        if (eventDispatcher is not null)
        {
            await eventDispatcher.DispatchAsync(
                new ProcessNodeEnteredEvent(instance.Id, node.Code, node.Name, assignees), ct);
        }

        return assignees;
    }

    public async Task<NodeAdvanceResult> TryAdvanceAsync(
        TenE0ProcessInstance instance,
        IProcessNode currentNode,
        ResolveContext resolveCtx,
        string actor,
        string? comment,
        CancellationToken ct = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);

        var nodeTasks = await dc.Set<TenE0ProcessTask>()
            .Where(t => t.InstanceId == instance.Id && t.NodeCode == currentNode.Code && !t.IsSoftDelete)
            .ToListAsync(ct);

        // 任何 Rejected → 实例终止（Reject handler 已处理实例状态，此处不应到达，但防御）
        if (nodeTasks.Any(t => t.Status == ProcessTaskStatus.Rejected))
            return new NodeAdvanceResult(false, null, [], ProcessStatus.Rejected);

        var mode = (currentNode as ApprovalNode)?.Mode ?? ApprovalMode.Single;
        var allApproved = nodeTasks.Count > 0 && nodeTasks.All(t => t.Status == ProcessTaskStatus.Approved);
        var anyApproved = nodeTasks.Any(t => t.Status == ProcessTaskStatus.Approved);

        // Countersign：全部 Approve 才通过；Single/OrSign：任一 Approve 即通过
        var passed = mode == ApprovalMode.Countersign ? allApproved : anyApproved;
        if (!passed)
            return new NodeAdvanceResult(false, null, [], null);

        // 通过 → 推进（穿越路由节点到首个任务节点或 End）
        var nextNode = await ResolveNextNodeAsync(instance, currentNode, resolveCtx, ct);
        var (finalNode, finalAssignees, completed) = await AdvanceThroughRouterNodesAsync(
            instance, nextNode, resolveCtx, ct);

        if (completed || finalNode is null or EndNode)
        {
            return new NodeAdvanceResult(true, finalNode?.Code ?? "end", [], ProcessStatus.Approved);
        }

        // 推进实例到最终停留节点
        var trackedInstance = await dc.Set<TenE0ProcessInstance>()
            .FirstAsync(i => i.Id == instance.Id, ct);
        trackedInstance.CurrentNodeCode = finalNode.Code;

        await dc.SaveChangesAsync(ct);

        return new NodeAdvanceResult(true, finalNode.Code, finalAssignees, null);
    }

    /// <summary>
    /// 穿越路由节点（Start/Branch）：这些节点无人审批，自动按 NextNodeCode/条件路由推进，
    /// 直到遇到 ApprovalNode/ParallelNode（需创建任务）或 EndNode（完成）或 null（异常终止）。
    /// </summary>
    private async Task<(IProcessNode? Node, IReadOnlyList<string> Assignees, bool Completed)> AdvanceThroughRouterNodesAsync(
        TenE0ProcessInstance instance,
        IProcessNode? startFrom,
        ResolveContext resolveCtx,
        CancellationToken ct)
    {
        var current = startFrom;
        // 防御性循环上限（理论无环，但分支配置错误时避免死循环）
        for (var i = 0; i < 64 && current is not null; i++)
        {
            switch (current)
            {
                case ApprovalNode or ParallelNode:
                    var assignees = await CreateTasksForNodeAsync(instance, current, resolveCtx, ct);
                    return (current, assignees, false);
                case EndNode:
                    return (current, [], false);
                case StartNode or BranchNode:
                    // 路由节点：继续解析下一节点
                    current = await ResolveNextNodeAsync(instance, current, resolveCtx, ct);
                    continue;
                default:
                    return (current, [], false);
            }
        }
        return (null, [], true);
    }

    /// <inheritdoc/>
    public async Task<NodeAdvanceResult> AdvanceToFirstTaskNodeAsync(
        TenE0ProcessInstance instance,
        IProcessNode fromNode,
        ResolveContext resolveCtx,
        CancellationToken ct = default)
    {
        var (finalNode, assignees, completed) = await AdvanceThroughRouterNodesAsync(instance, fromNode, resolveCtx, ct);

        if (completed || finalNode is null or EndNode)
        {
            // 流程立刻完成（无审批节点，Start→End）
            await using var dc = await contextFactory.CreateDbContextAsync(ct);
            var inst = await dc.Set<TenE0ProcessInstance>().FirstAsync(i => i.Id == instance.Id, ct);
            inst.Status = ProcessStatus.Approved;
            inst.CompletedAt = timeProvider.GetUtcNow();
            inst.CurrentNodeCode = finalNode?.Code ?? "end";
            await dc.SaveChangesAsync(ct);
            return new NodeAdvanceResult(true, finalNode?.Code ?? "end", [], ProcessStatus.Approved);
        }

        // 推进实例到首个任务节点
        await using var dc2 = await contextFactory.CreateDbContextAsync(ct);
        var tracked = await dc2.Set<TenE0ProcessInstance>().FirstAsync(i => i.Id == instance.Id, ct);
        tracked.CurrentNodeCode = finalNode.Code;
        await dc2.SaveChangesAsync(ct);

        return new NodeAdvanceResult(true, finalNode.Code, assignees, null);
    }

    public async Task<IProcessNode?> ResolveNextNodeAsync(
        TenE0ProcessInstance instance,
        IProcessNode currentNode,
        ResolveContext resolveCtx,
        CancellationToken ct = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);
        var def = await dc.Set<TenE0ProcessDefinition>()
            .FirstOrDefaultAsync(d => d.Id == instance.DefinitionId, ct);
        if (def is null) return null;

        var nodes = ProcessNodeSerializer.DeserializeNodes(def.NodesJson);
        var byCode = nodes.ToDictionary(n => n.Code);

        return currentNode switch
        {
            StartNode st => byCode.GetValueOrDefault(st.NextNodeCode),
            ApprovalNode an => string.IsNullOrEmpty(an.NextNodeCode)
                ? null : byCode.GetValueOrDefault(an.NextNodeCode),
            BranchNode bn => ResolveBranchTarget(bn, byCode, resolveCtx),
            ParallelNode pn => string.IsNullOrEmpty(pn.NextNodeCode)
                ? null : byCode.GetValueOrDefault(pn.NextNodeCode),
            EndNode => null,
            _ => null,
        };
    }

    private static IProcessNode? ResolveBranchTarget(
        BranchNode bn,
        IReadOnlyDictionary<string, IProcessNode> byCode,
        ResolveContext ctx)
    {
        foreach (var route in bn.Routes)
        {
            if (ConditionEvaluator.Evaluate(route.Condition, ctx.BusinessData, ctx.Initiator, ctx.InitiatorOrgId))
                return byCode.GetValueOrDefault(route.TargetNodeCode);
        }
        return string.IsNullOrEmpty(bn.DefaultNodeCode) ? null : byCode.GetValueOrDefault(bn.DefaultNodeCode);
    }

    private async Task<IReadOnlyList<string>> ResolveAssigneesAsync(
        IProcessNode node, ResolveContext ctx, CancellationToken ct)
    {
        switch (node)
        {
            case ApprovalNode an:
                return await ResolveViaPolicyAsync(an.AssigneePolicy, ctx, ct);
            case ParallelNode pn:
                {
                    var result = new List<string>();
                    foreach (var policy in pn.BranchPolicies)
                        result.AddRange(await ResolveViaPolicyAsync(policy, ctx, ct));
                    return result.Distinct().ToList();
                }
            case StartNode:
            case EndNode:
                return [];
            default:
                return [];
        }
    }

    private async Task<IReadOnlyList<string>> ResolveViaPolicyAsync(
        AssigneePolicy policy, ResolveContext ctx, CancellationToken ct)
    {
        if (!_resolvers.TryGetValue(policy.Kind, out var resolver))
            throw new InvalidOperationException(
                $"未注册 AssigneePolicyKind '{policy.Kind}' 的解析器。请通过 AddTenE0WorkflowDefinitions<TContext>() 注册内置 resolver 或自定义 resolver。");
        return await resolver.ResolveAsync(policy, ctx, ct);
    }
}
