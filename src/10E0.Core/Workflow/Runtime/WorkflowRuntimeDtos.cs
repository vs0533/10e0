namespace TenE0.Core.Workflow.Runtime;

/// <summary>启动流程请求。</summary>
public sealed record StartProcessRequest
{
    /// <summary>流程定义编码（取最新版本）。</summary>
    public required string DefinitionCode { get; init; }

    /// <summary>业务键。</summary>
    public required string BusinessKey { get; init; }

    /// <summary>业务实体类型名。</summary>
    public required string EntityType { get; init; }

    /// <summary>业务实体主键。</summary>
    public required string EntityId { get; init; }

    /// <summary>发起人。</summary>
    public required string Initiator { get; init; }

    /// <summary>发起人组织（用于上级解析）。</summary>
    public string? InitiatorOrgId { get; init; }

    /// <summary>标题。</summary>
    public string? Title { get; init; }

    /// <summary>业务数据（供分支条件求值 + 表达式解析）。</summary>
    public IReadOnlyDictionary<string, object?> BusinessData { get; init; } = new Dictionary<string, object?>();

    /// <summary>摘要（前端展示用，JSON）。</summary>
    public string? SummaryJson { get; init; }
}

/// <summary>启动流程结果。</summary>
public sealed record ProcessInstanceDto(
    string Id,
    string DefinitionCode,
    int DefinitionVersion,
    string BusinessKey,
    ProcessStatus Status,
    string CurrentNodeCode,
    string Initiator,
    string? Title,
    DateTimeOffset CreatedAt);

/// <summary>执行审批操作请求。</summary>
public sealed record ExecuteActionRequest
{
    public required string InstanceId { get; init; }
    public required ProcessActionKind Action { get; init; }
    public required string Actor { get; init; }
    public string? Comment { get; init; }

    /// <summary>Action=Delegate 时：被委派人。</summary>
    public string? DelegateTo { get; init; }

    /// <summary>Action=AddSigner 时：追加的审批人列表。</summary>
    public IReadOnlyList<string>? AddSigners { get; init; }

    /// <summary>Action=Rollback 时：回退目标节点。</summary>
    public string? RollbackToNodeCode { get; init; }
}

/// <summary>执行审批操作结果。</summary>
public sealed record ProcessActionResult(
    string InstanceId,
    ProcessStatus InstanceStatus,
    string? NextNodeCode,
    IReadOnlyList<string> NewTaskAssignees);

/// <summary>任务 DTO（待办列表用）。</summary>
public sealed record TaskDto(
    string Id,
    string InstanceId,
    string NodeCode,
    string Assignee,
    ProcessTaskStatus Status,
    DateTimeOffset? Deadline,
    string? BusinessKey,
    string? Title);

/// <summary>历史 DTO。</summary>
public sealed record HistoryDto(
    string Id,
    string NodeCode,
    string Action,
    string Actor,
    string? Assignee,
    string? Comment,
    DateTimeOffset Timestamp);

/// <summary>分页查询基础（运行时复用，避免依赖 Queries.PagedQuery 的泛型实体约束）。</summary>
public sealed record WorkflowPagedQuery(int Page = 1, int PageSize = 20);

/// <summary>分页结果。</summary>
public sealed record WorkflowPagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
