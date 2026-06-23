using TenE0.Core.DynamicFilters;
using TenE0.Core.Workflow.Definitions;

namespace TenE0.Core.Tests.Workflow.Definitions;

/// <summary>
/// #158 流程定义测试：Builder / Validator / 序列化往返。
/// </summary>
[Trait("Category", "Unit")]
public sealed class ProcessBuilderAndValidatorTests
{
    private sealed record ExpenseData(decimal Amount, string Reason);

    // 典型流程：start → manager(Single) → [Amount>10000? director(Countersign) : end] → end
    private static ProcessBuilder BuildExpenseBuilder() => ProcessBuilder.Create("expense", "费用报销")
        .Category("finance")
        .Start("start", "manager")
        .Approval("manager", "直属上级审批")
            .Assignee(AssigneePolicy.Manager())
            .Mode(ApprovalMode.Single)
            .Next("amount-check")
        .Branch("amount-check")
            .When("Amount", "gt", "10000", "director")
            .Default("end")
        .Approval("director", "总监审批")
            .Assignee(AssigneePolicy.Role("finance-director"))
            .Mode(ApprovalMode.Countersign)
            .Next("end")
        .End("end");

    // ================================================================
    // Builder + 序列化往返
    // ================================================================

    [Fact]
    public void Build_SequentialWithBranch_ProducesValidDefinition()
    {
        var def = BuildExpenseBuilder().Build();

        def.Code.Should().Be("expense");
        def.Name.Should().Be("费用报销");
        def.CategoryCode.Should().Be("finance");
        def.StartNodeCode.Should().Be("start");
        def.NodesJson.Should().NotBeNullOrEmpty();

        var nodes = ProcessNodeSerializer.DeserializeNodes(def.NodesJson);
        // start + manager + amount-check(branch) + director + end = 5
        nodes.Should().HaveCount(5);
        nodes.OfType<StartNode>().Should().ContainSingle();
        nodes.OfType<EndNode>().Should().ContainSingle();
        nodes.OfType<ApprovalNode>().Should().HaveCount(2);
        nodes.OfType<BranchNode>().Should().ContainSingle();
    }

    [Fact]
    public void Serialization_Roundtrip_PreservesNodeTypeAndFields()
    {
        var def = BuildExpenseBuilder().Build();

        var nodes = ProcessNodeSerializer.DeserializeNodes(def.NodesJson);

        var director = nodes.OfType<ApprovalNode>().Single(a => a.Code == "director");
        director.Mode.Should().Be(ApprovalMode.Countersign);
        director.AssigneePolicy.Kind.Should().Be(AssigneePolicyKind.Role);
        director.AssigneePolicy.RoleCode.Should().Be("finance-director");

        var branch = nodes.OfType<BranchNode>().Single();
        branch.Routes.Should().ContainSingle();
        branch.Routes[0].TargetNodeCode.Should().Be("director");
        branch.DefaultNodeCode.Should().Be("end");
    }

    // ================================================================
    // Validator 命中各类非法
    // ================================================================

    [Fact]
    public void Build_MissingStart_ThrowsInvalid()
    {
        var builder = ProcessBuilder.Create("p", "P")
            .Approval("a1", "审批").Assignee(AssigneePolicy.User("u1")).Done()
            .End("end");

        var act = () => builder.Build();

        var ex = act.Should().Throw<ProcessDefinitionInvalidException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("缺少 Start 节点"));
    }

    [Fact]
    public void Build_MultipleStarts_ThrowsInvalid()
    {
        var builder = ProcessBuilder.Create("p", "P")
            .Start("s1", "a1")
            .Start("s2", "a1")
            .Approval("a1", "审批").Assignee(AssigneePolicy.User("u1")).Done()
            .End("end");

        var act = () => builder.Build();

        var ex = act.Should().Throw<ProcessDefinitionInvalidException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("2 个 Start 节点"));
    }

    [Fact]
    public void Build_MissingEnd_ThrowsInvalid()
    {
        var builder = ProcessBuilder.Create("p", "P")
            .Start("s1", "a1")
            .Approval("a1", "审批").Assignee(AssigneePolicy.User("u1")).Next("end");

        var act = () => builder.Build();

        var ex = act.Should().Throw<ProcessDefinitionInvalidException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("缺少 End 节点"));
    }

    [Fact]
    public void Build_DanglingNextNodeCode_ThrowsInvalid()
    {
        var builder = ProcessBuilder.Create("p", "P")
            .Start("s1", "a1")
            .Approval("a1", "审批").Assignee(AssigneePolicy.User("u1")).Next("nonexistent")
            .End("end");

        var act = () => builder.Build();

        var ex = act.Should().Throw<ProcessDefinitionInvalidException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("nonexistent") && e.Contains("不存在"));
    }

    [Fact]
    public void Build_ApprovalNodeWithoutAssignee_ThrowsInvalid()
    {
        // 直接构造缺 AssigneePolicy 的节点集合（绕过 builder 的强制赋值）
        var nodes = new List<IProcessNode>
        {
            new StartNode { Code = "s", Name = "s", NextNodeCode = "a" },
            new ApprovalNode { Code = "a", Name = "a", NextNodeCode = "end", AssigneePolicy = null! },
            new EndNode { Code = "end", Name = "end" },
        };
        var validator = new ProcessDefinitionValidator();

        var errors = validator.Validate(nodes, "s");

        errors.Should().Contain(e => e.Contains("'a'") && e.Contains("AssigneePolicy"));
    }

    [Fact]
    public void Build_AllowRollbackWithoutTarget_ThrowsInvalid()
    {
        var nodes = new List<IProcessNode>
        {
            new StartNode { Code = "s", Name = "s", NextNodeCode = "a" },
            new ApprovalNode
            {
                Code = "a", Name = "a", NextNodeCode = "end",
                AssigneePolicy = AssigneePolicy.User("u1"),
                AllowRollback = true, // 但无 RollbackTargetCode
            },
            new EndNode { Code = "end", Name = "end" },
        };
        var validator = new ProcessDefinitionValidator();

        var errors = validator.Validate(nodes, "s");

        errors.Should().Contain(e => e.Contains("AllowRollback=true") && e.Contains("RollbackTargetCode"));
    }

    [Fact]
    public void Build_CycleDetected_ThrowsInvalid()
    {
        // a1 → a2 → a1（成环）
        var nodes = new List<IProcessNode>
        {
            new StartNode { Code = "s", Name = "s", NextNodeCode = "a1" },
            new ApprovalNode { Code = "a1", Name = "a1", NextNodeCode = "a2", AssigneePolicy = AssigneePolicy.User("u") },
            new ApprovalNode { Code = "a2", Name = "a2", NextNodeCode = "a1", AssigneePolicy = AssigneePolicy.User("u") },
            new EndNode { Code = "end", Name = "end" },
        };
        var validator = new ProcessDefinitionValidator();

        var errors = validator.Validate(nodes, "s");

        errors.Should().Contain(e => e.Contains("环路"));
    }

    [Fact]
    public void Build_RollbackTargetNotCountedAsCycle_Passes()
    {
        // a1 允许回退到 start，但正向流是 start→a1→end，不成环
        var nodes = new List<IProcessNode>
        {
            new StartNode { Code = "s", Name = "s", NextNodeCode = "a1" },
            new ApprovalNode
            {
                Code = "a1", Name = "a1", NextNodeCode = "end",
                AssigneePolicy = AssigneePolicy.User("u"),
                AllowRollback = true, RollbackTargetCode = "s",
            },
            new EndNode { Code = "end", Name = "end" },
        };
        var validator = new ProcessDefinitionValidator();

        var errors = validator.Validate(nodes, "s");

        errors.Should().NotContain(e => e.Contains("环路"));
    }

    [Fact]
    public void Build_BranchWithoutDefault_ThrowsInvalid()
    {
        var nodes = new List<IProcessNode>
        {
            new StartNode { Code = "s", Name = "s", NextNodeCode = "b" },
            new BranchNode
            {
                Code = "b", Name = "b",
                Routes = [new BranchRoute { TargetNodeCode = "a", Condition = new ConditionRuleGroup() }],
                DefaultNodeCode = null,
            },
            new ApprovalNode { Code = "a", Name = "a", NextNodeCode = "end", AssigneePolicy = AssigneePolicy.User("u") },
            new EndNode { Code = "end", Name = "end" },
        };
        var validator = new ProcessDefinitionValidator();

        var errors = validator.Validate(nodes, "s");

        errors.Should().Contain(e => e.Contains("'b'") && e.Contains("DefaultNodeCode"));
    }
}
