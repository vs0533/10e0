using Microsoft.EntityFrameworkCore;
using TenE0.Core.Events;
using TenE0.Core.Workflow.Definitions;

namespace TenE0.Core.Workflow.Runtime;

/// <summary>
/// 流程运行时服务实现。
///
/// 启动流程：取 latest 定义 → 创建实例（锁定版本）→ 解析首个审批节点 → 创建初始 Task → 触发 ProcessStartedEvent。
/// 执行操作：按 ActionKind 分发到对应 IProcessActionHandler。
/// </summary>
public sealed class ProcessRuntimeService<TContext>(
    IDbContextFactory<TContext> contextFactory,
    IProcessDefinitionStore definitionStore,
    IWorkflowEngine engine,
    IEnumerable<IProcessActionHandler> handlers,
    IDomainEventDispatcher? eventDispatcher,
    TimeProvider timeProvider) : IProcessRuntimeService
    where TContext : DbContext
{
    private readonly Dictionary<ProcessActionKind, IProcessActionHandler> _handlers =
        handlers.ToDictionary(h => h.ActionKind);

    public async Task<ProcessInstanceDto> StartAsync(StartProcessRequest req, CancellationToken ct = default)
    {
        var def = await definitionStore.GetLatestAsync(req.DefinitionCode, ct)
            ?? throw new InvalidOperationException($"流程定义 '{req.DefinitionCode}' 不存在或未发布");

        if (!def.IsEnabled)
            throw new InvalidOperationException($"流程定义 '{req.DefinitionCode}' 已禁用");

        var nodes = ProcessNodeSerializer.DeserializeNodes(def.NodesJson);
        var startNode = nodes.OfType<StartNode>().FirstOrDefault()
            ?? throw new InvalidOperationException("流程定义缺少 Start 节点");

        await using var dc = await contextFactory.CreateDbContextAsync(ct);

        var now = timeProvider.GetUtcNow();
        var instance = new TenE0ProcessInstance
        {
            DefinitionId = def.Id,
            DefinitionCode = def.Code,
            DefinitionVersion = def.Version,
            BusinessKey = req.BusinessKey,
            EntityType = req.EntityType,
            EntityId = req.EntityId,
            Status = ProcessStatus.Running,
            CurrentNodeCode = startNode.Code,
            Initiator = req.Initiator,
            InitiatorOrgId = req.InitiatorOrgId,
            Title = req.Title,
            SummaryJson = req.SummaryJson,
            CreateTime = now,
            CreateBy = req.Initiator,
        };
        dc.Set<TenE0ProcessInstance>().Add(instance);
        await dc.SaveChangesAsync(ct);

        // 首节点通常是 Start（无审批人），穿越路由节点到首个审批节点或 End
        var resolveCtx = new ResolveContext
        {
            Initiator = req.Initiator,
            InitiatorOrgId = req.InitiatorOrgId,
            TenantId = instance.TenantId,
            BusinessData = req.BusinessData,
        };

        // 写启动历史
        await using var dc2 = await contextFactory.CreateDbContextAsync(ct);
        dc2.Set<TenE0ProcessHistory>().Add(new TenE0ProcessHistory
        {
            InstanceId = instance.Id,
            NodeCode = startNode.Code,
            Action = "Start",
            Actor = req.Initiator,
            Timestamp = now,
        });
        await dc2.SaveChangesAsync(ct);

        // 推进到首个实际任务节点（穿越 Start/Branch 等路由节点）
        var advanceResult = await engine.AdvanceToFirstTaskNodeAsync(instance, startNode, resolveCtx, ct);
        var currentNodeCode = advanceResult.NextNodeCode ?? startNode.NextNodeCode;
        var initialAssignees = advanceResult.NewTaskAssignees;

        // 若启动即完成（Start→End 无审批节点）
        var finalStatus = advanceResult.InstanceFinalStatus ?? ProcessStatus.Running;

        // 触发启动事件
        if (eventDispatcher is not null)
        {
            await eventDispatcher.DispatchAsync(
                new ProcessStartedEvent(instance.Id, def.Code, def.Version, req.Initiator, currentNodeCode, initialAssignees), ct);
            if (finalStatus == ProcessStatus.Approved)
            {
                await eventDispatcher.DispatchAsync(
                    new ProcessCompletedEvent(instance.Id, req.Initiator, finalStatus, now), ct);
            }
        }

        return new ProcessInstanceDto(
            instance.Id, def.Code, def.Version, req.BusinessKey,
            finalStatus, currentNodeCode, req.Initiator, req.Title, now);
    }

    public async Task<ProcessActionResult> ExecuteActionAsync(ExecuteActionRequest req, CancellationToken ct = default)
    {
        if (!_handlers.TryGetValue(req.Action, out var handler))
            throw new InvalidOperationException($"未注册操作种类 '{req.Action}' 的处理器");

        // 🟡 review：乐观并发重试（对齐 #100 序列号模式）。
        // 两个并发审批同一 Task 时，RowVersion 冲突抛 DbUpdateConcurrencyException → 重试 →
        // 第二次重读发现 Task 状态已变（Approved/Rejected/Voided）→ handler 抛"没有待处理任务"。
        const int maxRetries = 3;
        Exception? lastError = null;
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                return await ExecuteActionCoreAsync(handler, req, ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                lastError = ex;
                if (attempt < maxRetries - 1)
                    await Task.Delay(Random.Shared.Next(5, 30), ct);
            }
        }

        // 重试耗尽：Task 可能已被另一并发审批处理，给出明确错误而非裸 500
        await using var dcFinal = await contextFactory.CreateDbContextAsync(ct);
        var task = await dcFinal.Set<TenE0ProcessTask>()
            .Where(t => t.InstanceId == req.InstanceId
                && t.Assignee == req.Actor
                && t.Status == ProcessTaskStatus.Pending)
            .FirstOrDefaultAsync(ct);
        if (task is null)
            throw new InvalidOperationException(
                $"操作冲突重试 {maxRetries} 次仍失败：用户 '{req.Actor}' 的待处理任务可能已被另一并发操作处理，请刷新后重试。",
                lastError);
        throw new InvalidOperationException(
            $"操作冲突重试 {maxRetries} 次仍失败，请稍后重试。", lastError);
    }

    private async Task<ProcessActionResult> ExecuteActionCoreAsync(
        IProcessActionHandler handler, ExecuteActionRequest req, CancellationToken ct)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);
        var instance = await dc.Set<TenE0ProcessInstance>()
            .FirstOrDefaultAsync(i => i.Id == req.InstanceId && !i.IsSoftDelete, ct)
            ?? throw new InvalidOperationException($"流程实例 '{req.InstanceId}' 不存在");

        if (instance.Status != ProcessStatus.Running)
            throw new InvalidOperationException($"流程实例已结束（状态={instance.Status}），不可再操作");

        var def = await dc.Set<TenE0ProcessDefinition>()
            .FirstAsync(d => d.Id == instance.DefinitionId, ct);
        var nodes = ProcessNodeSerializer.DeserializeNodes(def.NodesJson);
        var currentNode = nodes.First(n => n.Code == instance.CurrentNodeCode);

        var result = await handler.ExecuteAsync(instance, req, currentNode, ct);

        // 实例完成时触发完成事件
        if ((result.InstanceStatus == ProcessStatus.Approved || result.InstanceStatus == ProcessStatus.Rejected)
            && eventDispatcher is not null)
        {
            await using var dc3 = await contextFactory.CreateDbContextAsync(ct);
            var inst = await dc3.Set<TenE0ProcessInstance>().FirstAsync(i => i.Id == instance.Id, ct);
            await eventDispatcher.DispatchAsync(
                new ProcessCompletedEvent(instance.Id, inst.Initiator, result.InstanceStatus, inst.CompletedAt ?? timeProvider.GetUtcNow()), ct);
        }

        return result;
    }

    public async Task CancelAsync(string instanceId, string actor, string? reason, CancellationToken ct = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);
        var instance = await dc.Set<TenE0ProcessInstance>()
            .FirstOrDefaultAsync(i => i.Id == instanceId && !i.IsSoftDelete, ct)
            ?? throw new InvalidOperationException($"流程实例 '{instanceId}' 不存在");

        if (instance.Status != ProcessStatus.Running)
            throw new InvalidOperationException($"流程实例已结束（状态={instance.Status}），不可撤销");

        if (!string.Equals(instance.Initiator, actor, StringComparison.Ordinal))
            throw new InvalidOperationException($"仅发起人 '{instance.Initiator}' 可撤销流程，当前操作者 '{actor}' 无权限");

        instance.Status = ProcessStatus.Cancelled;
        instance.CompletedAt = timeProvider.GetUtcNow();

        // 当前节点 Pending Task 全部作废
        var pending = await dc.Set<TenE0ProcessTask>()
            .Where(t => t.InstanceId == instanceId && t.Status == ProcessTaskStatus.Pending)
            .ToListAsync(ct);
        foreach (var t in pending) t.Status = ProcessTaskStatus.Voided;

        dc.Set<TenE0ProcessHistory>().Add(new TenE0ProcessHistory
        {
            InstanceId = instanceId,
            NodeCode = instance.CurrentNodeCode,
            Action = "Cancel",
            Actor = actor,
            Comment = reason,
            Timestamp = timeProvider.GetUtcNow(),
        });

        await dc.SaveChangesAsync(ct);

        if (eventDispatcher is not null)
        {
            await eventDispatcher.DispatchAsync(
                new ProcessCancelledEvent(instanceId, instance.Initiator, reason), ct);
        }
    }
}
