using TenE0.Core.DynamicFilters;
using TenE0.Core.Workflow.Definitions;

namespace TenE0.Core.Tests.Workflow.Definitions;

/// <summary>
/// #158 条件求值测试 — 验证 ConditionEvaluator 对业务数据字典的正确求值。
/// </summary>
[Trait("Category", "Unit")]
public sealed class ConditionEvaluatorTests
{
    private static IReadOnlyDictionary<string, object?> Data(params (string, object?)[] kv)
        => kv.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact]
    public void Evaluate_Eq_NumberMatches_True()
    {
        var group = new ConditionRuleGroup
        {
            Rules = [new ConditionRule { Field = "Status", Op = "eq", Value = "Active" }],
        };
        var data = Data(("Status", "Active"));

        ConditionEvaluator.Evaluate(group, data).Should().BeTrue();
    }

    [Fact]
    public void Evaluate_Gt_Threshold_True()
    {
        var group = new ConditionRuleGroup
        {
            Rules = [new ConditionRule { Field = "Amount", Op = "gt", Value = "10000" }],
        };
        var data = Data(("Amount", 15000m));

        ConditionEvaluator.Evaluate(group, data).Should().BeTrue();
    }

    [Fact]
    public void Evaluate_Gt_AtThreshold_False()
    {
        var group = new ConditionRuleGroup
        {
            Rules = [new ConditionRule { Field = "Amount", Op = "gt", Value = "10000" }],
        };
        var data = Data(("Amount", 10000));

        ConditionEvaluator.Evaluate(group, data).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_AndLogic_AllMustPass()
    {
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules =
            [
                new ConditionRule { Field = "Amount", Op = "gt", Value = "1000" },
                new ConditionRule { Field = "Type", Op = "eq", Value = "Urgent" },
            ],
        };

        ConditionEvaluator.Evaluate(group, Data(("Amount", 5000), ("Type", "Urgent"))).Should().BeTrue();
        ConditionEvaluator.Evaluate(group, Data(("Amount", 5000), ("Type", "Normal"))).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_OrLogic_AnyPasses()
    {
        var group = new ConditionRuleGroup
        {
            Logic = "Or",
            Rules =
            [
                new ConditionRule { Field = "Amount", Op = "gt", Value = "10000" },
                new ConditionRule { Field = "Vip", Op = "eq", Value = "true" },
            ],
        };

        ConditionEvaluator.Evaluate(group, Data(("Amount", 500), ("Vip", "true"))).Should().BeTrue();
        ConditionEvaluator.Evaluate(group, Data(("Amount", 500), ("Vip", "false"))).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_NestedChildren_Recursive()
    {
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Region", Op = "eq", Value = "CN" }],
            Children =
            [
                new ConditionRuleGroup
                {
                    Logic = "Or",
                    Rules =
                    [
                        new ConditionRule { Field = "Amount", Op = "gt", Value = "1000" },
                        new ConditionRule { Field = "Level", Op = "eq", Value = "Gold" },
                    ],
                },
            ],
        };

        // Region=CN & (Amount>1000 | Level=Gold)
        ConditionEvaluator.Evaluate(group, Data(("Region", "CN"), ("Amount", 2000), ("Level", "Silver"))).Should().BeTrue();
        ConditionEvaluator.Evaluate(group, Data(("Region", "CN"), ("Amount", 500), ("Level", "Gold"))).Should().BeTrue();
        ConditionEvaluator.Evaluate(group, Data(("Region", "US"), ("Amount", 2000), ("Level", "Silver"))).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_LoginUserPlaceholder_ResolvedToInitiator()
    {
        var group = new ConditionRuleGroup
        {
            Rules = [new ConditionRule { Field = "Owner", Op = "eq", Value = "{loginUser}" }],
        };
        var data = Data(("Owner", "u999"));

        ConditionEvaluator.Evaluate(group, data, initiator: "u999").Should().BeTrue();
        ConditionEvaluator.Evaluate(group, data, initiator: "u888").Should().BeFalse();
    }

    [Fact]
    public void Evaluate_InOperator_CommaSeparatedString()
    {
        var group = new ConditionRuleGroup
        {
            Rules = [new ConditionRule { Field = "Dept", Op = "in", Value = "IT,HR,Finance" }],
        };

        ConditionEvaluator.Evaluate(group, Data(("Dept", "HR"))).Should().BeTrue();
        ConditionEvaluator.Evaluate(group, Data(("Dept", "Sales"))).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_Contains_Substring()
    {
        var group = new ConditionRuleGroup
        {
            Rules = [new ConditionRule { Field = "Title", Op = "contains", Value = "紧急" }],
        };

        ConditionEvaluator.Evaluate(group, Data(("Title", "这是紧急工单"))).Should().BeTrue();
        ConditionEvaluator.Evaluate(group, Data(("Title", "普通工单"))).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_MissingField_TreatedAsNull()
    {
        var group = new ConditionRuleGroup
        {
            Rules = [new ConditionRule { Field = "Missing", Op = "eq", Value = "X" }],
        };

        // 字段不存在 → actual=null，与 "X" 不等 → false
        ConditionEvaluator.Evaluate(group, Data()).Should().BeFalse();
    }
}
