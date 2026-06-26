using System.Text.Json.Serialization;
using TenE0.Core.DynamicFilters;

namespace TenE0.Core.Workflow.Definitions;

/// <summary>
/// 节点 JSON 序列化的 discriminator 名（多态 IProcessNode 序列化/反序列化）。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$nodeType")]
[JsonDerivedType(typeof(StartNode), nameof(NodeType.Start))]
[JsonDerivedType(typeof(EndNode), nameof(NodeType.End))]
[JsonDerivedType(typeof(ApprovalNode), nameof(NodeType.Approval))]
[JsonDerivedType(typeof(BranchNode), nameof(NodeType.Branch))]
[JsonDerivedType(typeof(ParallelNode), nameof(NodeType.Parallel))]
public interface IProcessNode
{
    /// <summary>节点编码（流程内唯一）。</summary>
    string Code { get; }

    /// <summary>显示名称。</summary>
    string Name { get; }

    /// <summary>节点类型。</summary>
    NodeType Type { get; }

    /// <summary>默认后继节点编码（Branch/Parallel 可空，由路由决定）。</summary>
    string? NextNodeCode { get; }
}

/// <summary>节点类型。</summary>
public enum NodeType
{
    /// <summary>开始节点（流程唯一入口）。</summary>
    Start,
    /// <summary>结束节点（至少一个）。</summary>
    End,
    /// <summary>审批节点（人审）。</summary>
    Approval,
    /// <summary>条件分支节点（按条件路由）。</summary>
    Branch,
    /// <summary>并行节点（多分支并发，全部完成才推进）。</summary>
    Parallel,
}

/// <summary>审批模式。</summary>
public enum ApprovalMode
{
    /// <summary>单人审批：任一审批人 Approve 即通过。</summary>
    Single,
    /// <summary>会签：所有审批人全部 Approve 才通过。</summary>
    Countersign,
    /// <summary>或签：N 人中任一 Approve 即通过（语义同 Single，区分在于"候选池"语义）。</summary>
    OrSign,
}

/// <summary>超时自动动作。</summary>
public enum TimeoutAction
{
    /// <summary>仅通知（触发事件，由订阅者推送）。</summary>
    NotifyOnly,
    /// <summary>自动通过。</summary>
    AutoApprove,
    /// <summary>自动驳回。</summary>
    AutoReject,
}

/// <summary>节点操作结果。</summary>
public enum NodeResult
{
    Approve,
    Reject,
    Delegate,
    Countersign,
}

// ============================================================
// 节点模型（用于内存操作 + JSON 序列化）
// ============================================================

/// <summary>开始节点。</summary>
public sealed class StartNode : IProcessNode
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "开始";
    public NodeType Type => NodeType.Start;
    public required string NextNodeCode { get; set; }
    string? IProcessNode.NextNodeCode => NextNodeCode;
}

/// <summary>结束节点。</summary>
public sealed class EndNode : IProcessNode
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "结束";
    public NodeType Type => NodeType.End;
    public string? NextNodeCode => null;
}

/// <summary>
/// 审批节点。
/// 每个审批人对应一条 Task（运行时由 #159 WorkflowEngine 创建）。
/// </summary>
public sealed class ApprovalNode : IProcessNode
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public NodeType Type => NodeType.Approval;

    /// <summary>审批模式。</summary>
    public ApprovalMode Mode { get; set; } = ApprovalMode.Single;

    /// <summary>审批人解析策略。</summary>
    public AssigneePolicy AssigneePolicy { get; set; } = AssigneePolicy.User([]);

    /// <summary>审批前校验的权限 key（可空 = 不校验权限）。</summary>
    public string? PermissionKey { get; set; }

    /// <summary>是否允许委派。</summary>
    public bool AllowDelegate { get; set; }

    /// <summary>是否允许加签。</summary>
    public bool AllowAddSigner { get; set; }

    /// <summary>是否允许回退。</summary>
    public bool AllowRollback { get; set; }

    /// <summary>回退目标节点编码（AllowRollback=true 时必填）。</summary>
    public string? RollbackTargetCode { get; set; }

    /// <summary>节点超时时长（null = 不超时）。</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>超时自动动作。</summary>
    public TimeoutAction TimeoutAction { get; set; } = TimeoutAction.NotifyOnly;

    /// <summary>默认后继节点（审批通过后推进到此处）。</summary>
    public string? NextNodeCode { get; set; }
}

/// <summary>
/// 条件分支节点：按条件路由到不同后继。
/// 每条 <see cref="BranchRoute"/> 用 <see cref="ConditionRuleGroup"/>（复用 DynamicFilters）表达条件，
/// 求值对象是业务数据字典（详见 <c>ConditionEvaluator</c>）。
/// </summary>
public sealed class BranchNode : IProcessNode
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public NodeType Type => NodeType.Branch;

    /// <summary>分支路由列表（按声明顺序匹配，首个命中即采用）。</summary>
    public List<BranchRoute> Routes { get; set; } = [];

    /// <summary>所有条件都不命中时的默认后继（null = 无默认，流程报错）。</summary>
    public string? DefaultNodeCode { get; set; }

    /// <summary>分支节点无单一 NextNodeCode（由路由决定）。</summary>
    public string? NextNodeCode => DefaultNodeCode;
}

/// <summary>分支路由：条件 + 目标节点。</summary>
public sealed class BranchRoute
{
    /// <summary>目标节点编码。</summary>
    public required string TargetNodeCode { get; set; }

    /// <summary>路由条件（复用 DynamicFilters.ConditionRuleGroup）。</summary>
    public ConditionRuleGroup Condition { get; set; } = new();
}

/// <summary>
/// 并行节点：多分支并发推进，全部完成才合并推进到 <see cref="NextNodeCode"/>。
///
/// 注：本期并行节点实现"全部 Approve 才推进"的 join 语义；
/// 复杂的 fork/join 图由后续 epic 覆盖。
/// </summary>
public sealed class ParallelNode : IProcessNode
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public NodeType Type => NodeType.Parallel;

    /// <summary>并行分支的审批人策略列表（每个策略产生一个并发 Task）。</summary>
    public List<AssigneePolicy> BranchPolicies { get; set; } = [];

    /// <summary>全部分支完成后推进到的节点。</summary>
    public string? NextNodeCode { get; set; }
}
