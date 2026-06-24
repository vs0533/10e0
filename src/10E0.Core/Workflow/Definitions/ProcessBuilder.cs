using TenE0.Core.DynamicFilters;

namespace TenE0.Core.Workflow.Definitions;

/// <summary>
/// 流程定义 Fluent API 构造器。
///
/// 用法：
/// <code>
/// var def = ProcessBuilder.Create("expense-claim", "费用报销")
///     .Category("finance")
///     .Start("start")
///     .Approval("manager", "直属上级审批")
///         .Assignee(AssigneePolicy.Manager())
///         .Mode(ApprovalMode.Single)
///         .Permission("expense.approve")
///     .Branch("amount-check")
///         .When("Amount", "gt", "10000", "director")
///         .Default("end")
///     .Approval("director", "总监审批")
///         .Assignee(AssigneePolicy.Role("finance-director"))
///         .Mode(ApprovalMode.Countersign)
///     .End("end")
///     .Build();  // 返回 TenE0ProcessDefinition 实体（含序列化 NodesJson + 校验）
/// </code>
/// </summary>
public sealed class ProcessBuilder
{
    private readonly string _code;
    private string _name;
    private string? _category;
    private string? _description;
    private readonly List<IProcessNode> _nodes = [];
    private ApprovalBuildContext? _pendingApproval;
    private BranchBuildContext? _pendingBranch;

    private ProcessBuilder(string code, string name)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("流程编码不能为空", nameof(code));
        _code = code;
        _name = name;
    }

    public static ProcessBuilder Create(string code, string name) => new(code, name);

    public ProcessBuilder Category(string category) { _category = category; return this; }
    public ProcessBuilder Description(string description) { _description = description; return this; }

    /// <summary>声明 Start 节点（流程唯一入口），并指定首个后继节点。</summary>
    public ProcessBuilder Start(string code, string nextNodeCode)
    {
        FlushPending();
        _nodes.Add(new StartNode { Code = code, Name = "开始", NextNodeCode = nextNodeCode });
        return this;
    }

    /// <summary>声明 Approval 节点，返回 Approval 配置上下文（链式配置审批人/模式/权限等）。</summary>
    public ApprovalBuildContext Approval(string code, string name)
    {
        FlushPending();
        var ctx = new ApprovalBuildContext(this, new ApprovalNode { Code = code, Name = name });
        _pendingApproval = ctx;
        return ctx;
    }

    /// <summary>声明 Branch 节点，返回 Branch 配置上下文。</summary>
    public BranchBuildContext Branch(string code, string name = "条件分支")
    {
        FlushPending();
        var ctx = new BranchBuildContext(this, new BranchNode { Code = code, Name = name });
        _pendingBranch = ctx;
        return ctx;
    }

    /// <summary>声明 End 节点。</summary>
    public ProcessBuilder End(string code)
    {
        FlushPending();
        _nodes.Add(new EndNode { Code = code, Name = "结束" });
        return this;
    }

    /// <summary>声明 Parallel 节点（返回 Parallel 配置上下文）。</summary>
    public ParallelBuildContext Parallel(string code, string name = "并行审批")
    {
        FlushPending();
        var ctx = new ParallelBuildContext(this, new ParallelNode { Code = code, Name = name });
        return ctx;
    }

    /// <summary>
    /// 构建 + 校验，返回实体（不含 Id/时间戳，由 EF 填充）。
    /// 校验失败抛 <see cref="ProcessDefinitionInvalidException"/>。
    /// </summary>
    public TenE0ProcessDefinition Build(IProcessDefinitionValidator? validator = null)
    {
        FlushPending();
        validator ??= new ProcessDefinitionValidator();

        var start = _nodes.OfType<StartNode>().FirstOrDefault()
            ?? throw new ProcessDefinitionInvalidException(["缺少 Start 节点"]);

        validator.ValidateOrThrow(_nodes, start.Code);

        var nodesJson = ProcessNodeSerializer.SerializeNodes(_nodes);

        return new TenE0ProcessDefinition
        {
            Code = _code,
            Name = _name,
            Version = 1,
            CategoryCode = _category,
            Description = _description,
            StartNodeCode = start.Code,
            NodesJson = nodesJson,
            EdgesJson = "[]",
            IsEnabled = true,
            IsLatest = true,
        };
    }

    internal void AddNode(IProcessNode node)
    {
        FlushPending();
        _nodes.Add(node);
    }

    private void FlushPending()
    {
        if (_pendingApproval is not null)
        {
            _nodes.Add(_pendingApproval.Node);
            _pendingApproval = null;
        }
        if (_pendingBranch is not null)
        {
            _nodes.Add(_pendingBranch.Node);
            _pendingBranch = null;
        }
    }

    // ── Approval 配置上下文 ──

    public sealed class ApprovalBuildContext(ProcessBuilder owner, ApprovalNode node)
    {
        public ApprovalNode Node { get; } = node;
        private readonly ProcessBuilder _owner = owner;

        public ApprovalBuildContext Assignee(AssigneePolicy policy) { Node.AssigneePolicy = policy; return this; }
        public ApprovalBuildContext Mode(ApprovalMode mode) { Node.Mode = mode; return this; }
        public ApprovalBuildContext Permission(string key) { Node.PermissionKey = key; return this; }
        public ApprovalBuildContext AllowDelegate() { Node.AllowDelegate = true; return this; }
        public ApprovalBuildContext AllowAddSigner() { Node.AllowAddSigner = true; return this; }
        public ApprovalBuildContext AllowRollback(string targetCode) { Node.AllowRollback = true; Node.RollbackTargetCode = targetCode; return this; }
        public ApprovalBuildContext Timeout(TimeSpan ts, TimeoutAction action = TimeoutAction.NotifyOnly) { Node.Timeout = ts; Node.TimeoutAction = action; return this; }

        /// <summary>设置审批通过后推进的下一节点，并返回 owner 继续声明下一个节点。</summary>
        public ProcessBuilder Next(string nextNodeCode) { Node.NextNodeCode = nextNodeCode; return _owner; }

        /// <summary>不显式设 Next（由后续节点反向引用时设置），返回 owner。</summary>
        public ProcessBuilder Done() => _owner;
    }

    // ── Branch 配置上下文 ──

    public sealed class BranchBuildContext(ProcessBuilder owner, BranchNode node)
    {
        public BranchNode Node { get; } = node;
        private readonly ProcessBuilder _owner = owner;

        /// <summary>添加条件路由（业务字段 op 值 → 目标节点）。</summary>
        public BranchBuildContext When(string field, string op, string value, string targetNodeCode)
        {
            Node.Routes.Add(new BranchRoute
            {
                TargetNodeCode = targetNodeCode,
                Condition = new ConditionRuleGroup
                {
                    Logic = "And",
                    Rules = [new ConditionRule { Field = field, Op = op, Value = value }],
                },
            });
            return this;
        }

        /// <summary>添加条件路由（自定义 ConditionRuleGroup）。</summary>
        public BranchBuildContext WhenGroup(ConditionRuleGroup condition, string targetNodeCode)
        {
            Node.Routes.Add(new BranchRoute { TargetNodeCode = targetNodeCode, Condition = condition });
            return this;
        }

        /// <summary>所有条件都不命中时的默认目标。</summary>
        public ProcessBuilder Default(string defaultNodeCode)
        {
            Node.DefaultNodeCode = defaultNodeCode;
            return _owner;
        }
    }

    // ── Parallel 配置上下文 ──

    public sealed class ParallelBuildContext(ProcessBuilder owner, ParallelNode node)
    {
        public ParallelNode Node { get; } = node;
        private readonly ProcessBuilder _owner = owner;

        public ParallelBuildContext BranchWith(AssigneePolicy policy)
        {
            Node.BranchPolicies.Add(policy);
            return this;
        }

        /// <summary>全部分支完成后推进到的节点。</summary>
        public ProcessBuilder Join(string nextNodeCode)
        {
            Node.NextNodeCode = nextNodeCode;
            _owner.AddNode(Node);
            return _owner;
        }
    }
}
