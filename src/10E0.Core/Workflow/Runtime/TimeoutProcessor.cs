using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenE0.Core.Events;
using TenE0.Core.Workflow.Definitions;

namespace TenE0.Core.Workflow.Runtime;

/// <summary>超时处理器配置。</summary>
public sealed class WorkflowRuntimeOptions
{
    /// <summary>超时扫描间隔（默认 1 分钟）。</summary>
    public TimeSpan TimeoutScanInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>每次扫描处理的任务上限（避免一次性处理过多）。</summary>
    public int TimeoutBatchSize { get; set; } = 100;
}

/// <summary>
/// 超时任务后台处理器 — 定期扫描 <c>Status=Pending AND Deadline &lt; now</c> 的任务，
/// 按节点 <see cref="ApprovalNode.TimeoutAction"/> 执行。
///
/// 模式参考 <c>OutboxRelayService</c>：BackgroundService + IDbContextFactory。
/// 推送（NotifyOnly）由事件订阅者（#155）负责，本处理器只改 Task 状态 + 触发事件。
/// </summary>
public sealed class TimeoutProcessor<TContext>(
    IDbContextFactory<TContext> contextFactory,
    IDomainEventDispatcher? eventDispatcher,
    IOptions<WorkflowRuntimeOptions> options,
    ILogger<TimeoutProcessor<TContext>>? logger,
    TimeProvider timeProvider) : BackgroundService
    where TContext : DbContext
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.TimeoutScanInterval;
        logger?.LogInformation("Workflow TimeoutProcessor 启动，扫描间隔 {Interval}", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger?.LogError(ex, "Workflow TimeoutProcessor 批处理异常");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        await using var dc = await contextFactory.CreateDbContextAsync(ct);

        var expired = await dc.Set<TenE0ProcessTask>()
            .Where(t => t.Status == ProcessTaskStatus.Pending && t.Deadline != null && t.Deadline < now)
            .OrderBy(t => t.Deadline)
            .Take(options.Value.TimeoutBatchSize)
            .ToListAsync(ct);

        if (expired.Count == 0) return;

        foreach (var task in expired)
        {
            await ProcessTaskAsync(dc, task, now, ct);
        }

        await dc.SaveChangesAsync(ct);
    }

    private async Task ProcessTaskAsync(DbContext dc, TenE0ProcessTask task, DateTimeOffset now, CancellationToken ct)
    {
        // 取实例 + 定义 + 节点，确定 TimeoutAction
        var instance = await dc.Set<TenE0ProcessInstance>()
            .FirstOrDefaultAsync(i => i.Id == task.InstanceId, ct);
        if (instance is null || instance.Status != ProcessStatus.Running) return;

        var def = await dc.Set<TenE0ProcessDefinition>()
            .FirstOrDefaultAsync(d => d.Id == instance.DefinitionId, ct);
        if (def is null) return;

        var nodes = ProcessNodeSerializer.DeserializeNodes(def.NodesJson);
        var node = nodes.OfType<ApprovalNode>().FirstOrDefault(n => n.Code == task.NodeCode);
        if (node is null) return;

        var action = node.TimeoutAction;
        task.CompletedAt = now;
        task.CompletedBy = "system:timeout";
        task.Comment = $"任务超时（Deadline={task.Deadline:O}）";

        switch (action)
        {
            case TimeoutAction.AutoApprove:
                task.Status = ProcessTaskStatus.Approved;
                dc.Set<TenE0ProcessHistory>().Add(new TenE0ProcessHistory
                {
                    InstanceId = task.InstanceId, NodeCode = task.NodeCode,
                    Action = "TimeoutAutoApprove", Actor = "system:timeout", Timestamp = now,
                });
                break;
            case TimeoutAction.AutoReject:
                task.Status = ProcessTaskStatus.Rejected;
                dc.Set<TenE0ProcessHistory>().Add(new TenE0ProcessHistory
                {
                    InstanceId = task.InstanceId, NodeCode = task.NodeCode,
                    Action = "TimeoutAutoReject", Actor = "system:timeout", Timestamp = now,
                });
                break;
            case TimeoutAction.NotifyOnly:
            default:
                task.Status = ProcessTaskStatus.Timeout;
                dc.Set<TenE0ProcessHistory>().Add(new TenE0ProcessHistory
                {
                    InstanceId = task.InstanceId, NodeCode = task.NodeCode,
                    Action = "TimeoutNotify", Actor = "system:timeout", Timestamp = now,
                });
                break;
        }

        logger?.LogInformation("任务 {TaskId} 超时处理：Action={Action}", task.Id, action);

        // 触发节点进入事件（NotifyOnly 场景供推送订阅提醒）
        if (eventDispatcher is not null && action == TimeoutAction.NotifyOnly)
        {
            await eventDispatcher.DispatchAsync(
                new ProcessNodeEnteredEvent(instance.Id, task.NodeCode, node.Name, [task.Assignee]), ct);
        }
    }
}
