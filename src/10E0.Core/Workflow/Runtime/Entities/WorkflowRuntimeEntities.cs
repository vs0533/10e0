using TenE0.Core.Abstractions;
using TenE0.Core.Entities;

namespace TenE0.Core.Workflow.Runtime;

/// <summary>
/// 流程实例 — 一次具体的审批（绑定业务实体 + 锁定 Definition 版本）。
///
/// 关键字段：
/// <list type="bullet">
/// <item><see cref="DefinitionVersion"/>：实例创建时锁定的版本，模板改版不影响存量。</item>
/// <item><see cref="BusinessKey"/>+<see cref="EntityType"/>+<see cref="EntityId"/>：弱关联业务实体。</item>
/// <item><see cref="RowVersion"/>（shadow property）：乐观并发控制，防并发审批冲突。</item>
/// </list>
/// </summary>
public class TenE0ProcessInstance : AuditedEntity, IMultiTenantEntity
{
    /// <summary>关联的流程定义 Id（具体版本）。</summary>
    public string DefinitionId { get; set; } = "";

    /// <summary>流程定义编码（冗余，便于查询不 join 定义表）。</summary>
    public string DefinitionCode { get; set; } = "";

    /// <summary>锁定的流程定义版本号。</summary>
    public int DefinitionVersion { get; set; }

    /// <summary>业务键（如 "EXP-2026-0001"）。</summary>
    public string BusinessKey { get; set; } = "";

    /// <summary>业务实体类型名。</summary>
    public string EntityType { get; set; } = "";

    /// <summary>业务实体主键。</summary>
    public string EntityId { get; set; } = "";

    /// <summary>实例状态。</summary>
    public ProcessStatus Status { get; set; } = ProcessStatus.Running;

    /// <summary>当前节点编码。</summary>
    public string CurrentNodeCode { get; set; } = "";

    /// <summary>发起人。</summary>
    public string Initiator { get; set; } = "";

    /// <summary>发起人组织（用于上级解析）。</summary>
    public string? InitiatorOrgId { get; set; }

    /// <summary>标题（展示用）。</summary>
    public string? Title { get; set; }

    /// <summary>摘要 JSON（前端展示用，不依赖业务表）。</summary>
    public string? SummaryJson { get; set; }

    /// <summary>完成时间。</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>租户 ID。</summary>
    public string TenantId { get; set; } = "";
}

/// <summary>
/// 审批任务 — 当前节点分配给某个审批人的待办。
/// 每个审批人一条 Task（便于委派/加签/独立超时）。
/// </summary>
public class TenE0ProcessTask : AuditedEntity
{
    /// <summary>所属流程实例 Id。</summary>
    public string InstanceId { get; set; } = "";

    /// <summary>任务所在节点编码。</summary>
    public string NodeCode { get; set; } = "";

    /// <summary>被分配人（用户编码）。</summary>
    public string Assignee { get; set; } = "";

    /// <summary>委派来源（如由委派产生，记录委派人）。</summary>
    public string? DelegatedBy { get; set; }

    /// <summary>任务状态。</summary>
    public ProcessTaskStatus Status { get; set; } = ProcessTaskStatus.Pending;

    /// <summary>完成时间。</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>完成人（通常等于 Assignee，委派场景为被委派人）。</summary>
    public string? CompletedBy { get; set; }

    /// <summary>审批意见。</summary>
    public string? Comment { get; set; }

    /// <summary>截止时间（超时阈值）。</summary>
    public DateTimeOffset? Deadline { get; set; }
}

/// <summary>
/// 流程历史 — 只追加的审计轨迹（谁在何时对哪个节点做了什么）。
/// 不修改不删除，保证审计完整性。
/// </summary>
public class TenE0ProcessHistory : BaseEntity
{
    /// <summary>所属流程实例 Id。</summary>
    public string InstanceId { get; set; } = "";

    /// <summary>动作发生时所在的节点编码。</summary>
    public string NodeCode { get; set; } = "";

    /// <summary>动作种类（Start/Approve/Reject/Delegate/AddSigner/Rollback/Cancel/Timeout）。</summary>
    public string Action { get; set; } = "";

    /// <summary>操作者。</summary>
    public string Actor { get; set; } = "";

    /// <summary>操作对象（如委派时的被委派人）。</summary>
    public string? Assignee { get; set; }

    /// <summary>审批意见。</summary>
    public string? Comment { get; set; }

    /// <summary>动作时间戳。</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
