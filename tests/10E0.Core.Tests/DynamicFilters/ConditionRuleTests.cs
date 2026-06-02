using TenE0.Core.DynamicFilters;

namespace TenE0.Core.Tests.DynamicFilters;

public sealed class ConditionRuleTests
{
    [Fact]
    public void ConditionRuleGroup_Defaults_ShouldBeCorrect()
    {
        var group = new ConditionRuleGroup();

        group.Logic.Should().Be("And");
        group.Rules.Should().BeEmpty();
        group.Children.Should().BeEmpty();
    }

    [Fact]
    public void ConditionRuleGroup_CanSetLogicToOr()
    {
        var group = new ConditionRuleGroup { Logic = "Or" };

        group.Logic.Should().Be("Or");
    }

    [Fact]
    public void ConditionRule_CanSetAllProperties()
    {
        var rule = new ConditionRule
        {
            Field = "CreateBy",
            Op = "eq",
            Value = "{loginUser}"
        };

        rule.Field.Should().Be("CreateBy");
        rule.Op.Should().Be("eq");
        rule.Value.Should().Be("{loginUser}");
    }

    [Fact]
    public void ConditionRuleGroup_CanBuildNestedStructure()
    {
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules =
            [
                new ConditionRule { Field = "CreateBy", Op = "eq", Value = "{loginUser}" },
                new ConditionRule { Field = "Status", Op = "eq", Value = "Active" }
            ],
            Children =
            [
                new ConditionRuleGroup
                {
                    Logic = "Or",
                    Rules =
                    [
                        new ConditionRule { Field = "OrgId", Op = "in", Value = "{loginOrg}" }
                    ]
                }
            ]
        };

        group.Rules.Should().HaveCount(2);
        group.Children.Should().HaveCount(1);
        group.Children[0].Rules.Should().HaveCount(1);
        group.Children[0].Logic.Should().Be("Or");
    }

    [Fact]
    public void DataFilterRuleCreateRequest_Constructor_ShouldSetAllFields()
    {
        var req = new DataFilterRuleCreateRequest(
            "Orders",
            "{}",
            "Created by user filter",
            true);

        req.EntityTypeName.Should().Be("Orders");
        req.RuleJson.Should().Be("{}");
        req.Description.Should().Be("Created by user filter");
        req.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void DataFilterRuleCreateRequest_DefaultIsEnabled_ShouldBeTrue()
    {
        var req = new DataFilterRuleCreateRequest("Users", "{}", null);

        req.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void DataFilterRuleUpdateRequest_AllFields_ShouldBeNullByDefault()
    {
        var req = new DataFilterRuleUpdateRequest(null, null, null);

        req.RuleJson.Should().BeNull();
        req.Description.Should().BeNull();
        req.IsEnabled.Should().BeNull();
    }

    [Fact]
    public void DataFilterRuleUpdateRequest_PartialUpdate_ShouldPreserveNonNull()
    {
        var req = new DataFilterRuleUpdateRequest("{}", "Updated description", false);

        req.RuleJson.Should().Be("{}");
        req.Description.Should().Be("Updated description");
        req.IsEnabled.Should().BeFalse();
    }
}
