using Microsoft.EntityFrameworkCore;

namespace TenE0.Core.Workflow.Runtime;

/// <summary>
/// 待办 / 历史 / 实例查询服务实现。
/// </summary>
public sealed class TaskService<TContext>(IDbContextFactory<TContext> contextFactory) : ITaskService
    where TContext : DbContext
{
    public async Task<WorkflowPagedResult<TaskDto>> GetMyPendingTasksAsync(
        string userCode, WorkflowPagedQuery query, CancellationToken ct = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);

        var q = from t in dc.Set<TenE0ProcessTask>()
                join i in dc.Set<TenE0ProcessInstance>() on t.InstanceId equals i.Id
                where t.Assignee == userCode && t.Status == ProcessTaskStatus.Pending && !t.IsSoftDelete && !i.IsSoftDelete
                orderby t.CreateTime descending
                select new { t, i };

        var total = await q.CountAsync(ct);
        var rows = await q.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(ct);

        var items = rows.Select(r => new TaskDto(
            r.t.Id, r.t.InstanceId, r.t.NodeCode, r.t.Assignee, r.t.Status, r.t.Deadline,
            r.i.BusinessKey, r.i.Title)).ToList();

        return new WorkflowPagedResult<TaskDto>(items, total, query.Page, query.PageSize);
    }

    public async Task<WorkflowPagedResult<ProcessInstanceDto>> GetMyInitiatedAsync(
        string userCode, WorkflowPagedQuery query, CancellationToken ct = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);

        var q = dc.Set<TenE0ProcessInstance>()
            .Where(i => i.Initiator == userCode && !i.IsSoftDelete)
            .OrderByDescending(i => i.CreateTime);

        var total = await q.CountAsync(ct);
        var rows = await q.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(ct);

        var items = rows.Select(i => new ProcessInstanceDto(
            i.Id, i.DefinitionCode, i.DefinitionVersion, i.BusinessKey,
            i.Status, i.CurrentNodeCode, i.Initiator, i.Title, i.CreateTime ?? DateTimeOffset.UtcNow)).ToList();

        return new WorkflowPagedResult<ProcessInstanceDto>(items, total, query.Page, query.PageSize);
    }

    public async Task<IReadOnlyList<HistoryDto>> GetInstanceHistoryAsync(string instanceId, CancellationToken ct = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);
        var list = await dc.Set<TenE0ProcessHistory>()
            .Where(h => h.InstanceId == instanceId)
            .OrderBy(h => h.Timestamp)
            .Select(h => new HistoryDto(h.Id, h.NodeCode, h.Action, h.Actor, h.Assignee, h.Comment, h.Timestamp))
            .ToListAsync(ct);
        return list;
    }

    public async Task<ProcessInstanceDto?> GetInstanceAsync(string instanceId, CancellationToken ct = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);
        var i = await dc.Set<TenE0ProcessInstance>()
            .Where(i => i.Id == instanceId && !i.IsSoftDelete)
            .FirstOrDefaultAsync(ct);
        if (i is null) return null;
        return new ProcessInstanceDto(
            i.Id, i.DefinitionCode, i.DefinitionVersion, i.BusinessKey,
            i.Status, i.CurrentNodeCode, i.Initiator, i.Title, i.CreateTime ?? DateTimeOffset.UtcNow);
    }
}
