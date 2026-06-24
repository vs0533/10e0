namespace TenE0.Core.Workflow.Runtime;

/// <summary>
/// 流程运行时服务 — 启动 / 审批操作 / 撤销。
/// </summary>
public interface IProcessRuntimeService
{
    /// <summary>发起流程：创建实例 + 解析首个节点审批人 + 创建初始 Task。</summary>
    Task<ProcessInstanceDto> StartAsync(StartProcessRequest req, CancellationToken ct = default);

    /// <summary>执行审批操作（按 ActionKind 分发到对应 handler）。</summary>
    Task<ProcessActionResult> ExecuteActionAsync(ExecuteActionRequest req, CancellationToken ct = default);

    /// <summary>撤销流程（仅发起人 / admin 可调用）。</summary>
    Task CancelAsync(string instanceId, string actor, string? reason, CancellationToken ct = default);
}

/// <summary>
/// 待办任务查询服务。
/// </summary>
public interface ITaskService
{
    /// <summary>我的待办。</summary>
    Task<WorkflowPagedResult<TaskDto>> GetMyPendingTasksAsync(string userCode, WorkflowPagedQuery query, CancellationToken ct = default);

    /// <summary>我发起的流程实例。</summary>
    Task<WorkflowPagedResult<ProcessInstanceDto>> GetMyInitiatedAsync(string userCode, WorkflowPagedQuery query, CancellationToken ct = default);

    /// <summary>实例的审批历史（按时间正序）。</summary>
    Task<IReadOnlyList<HistoryDto>> GetInstanceHistoryAsync(string instanceId, CancellationToken ct = default);

    /// <summary>实例详情（当前节点 + 状态）。</summary>
    Task<ProcessInstanceDto?> GetInstanceAsync(string instanceId, CancellationToken ct = default);
}
