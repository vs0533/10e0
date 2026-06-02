using System.Text.Json;
using TenE0.Core.Abstractions;
using TenE0.Core.DataContext;
using TenE0.Core.DynamicFilters;
using TenE0.Core.Permissions.DataFilter;

namespace TenE0.Core.Tests.DynamicFilters;

[Trait("Category", "Unit")]
public sealed class FilterExpressionBuilderTests
{
    // ─── Test Entity & Enum ──────────────────────────────────────

    private enum StatusEnum
    {
        Active,
        Inactive
    }

    private sealed class TestEntity
    {
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
        public long Count { get; set; }
        public Guid DepartmentId { get; set; }
        public DateTime Created { get; set; }
        public DateTimeOffset Modified { get; set; }
        public bool Active { get; set; }
        public decimal Price { get; set; }
        public float Rating { get; set; }
        public double Score { get; set; }
        public StatusEnum Status { get; set; }
        public string? Description { get; set; }
    }

    // ─── Test DbContext ──────────────────────────────────────────

    private sealed class TestDbContext : BaseDataContext
    {
        public TestDbContext(
            DbContextOptions options,
            ICurrentUserContext currentUser,
            IDataAccessPolicy accessPolicy)
            : base(options, currentUser, accessPolicy,
                   Enumerable.Empty<IEntityFilterContributor>(),
                   Mock.Of<IDynamicFilterProvider>())
        {
        }
    }

    // ─── Mocks & Helpers ─────────────────────────────────────────

    private static Mock<ICurrentUserContext> CreateUserMock(
        string userCode = "test-user",
        string[]? roleIds = null,
        bool isAuthenticated = true)
    {
        var mock = new Mock<ICurrentUserContext>();
        mock.SetupGet(c => c.UserCode).Returns(userCode);
        mock.SetupGet(c => c.RoleIds).Returns(roleIds ?? new[] { "admin", "user" });
        mock.SetupGet(c => c.IsAuthenticated).Returns(isAuthenticated);
        mock.SetupGet(c => c.UserType).Returns(UserType.Person);
        return mock;
    }

    private static Mock<IDataAccessPolicy> CreatePolicyMock(bool bypassFilters = false)
    {
        var mock = new Mock<IDataAccessPolicy>();
        mock.SetupGet(p => p.BypassFilters).Returns(bypassFilters);
        return mock;
    }

    private static TestDbContext CreateDbContext(
        bool bypassFilters = false,
        string userCode = "test-user",
        string[]? roleIds = null)
    {
        var currentUser = CreateUserMock(userCode, roleIds);
        var policy = CreatePolicyMock(bypassFilters);
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestDbContext(options, currentUser.Object, policy.Object);
    }

    private static string SerializeGroup(ConditionRuleGroup group)
        => JsonSerializer.Serialize(group);

    // ─── Compile Helper ──────────────────────────────────────────

    private static Func<TestEntity, bool> BuildAndCompile(
        string json,
        BaseDataContext context)
    {
        var lambda = FilterExpressionBuilder.Build(
            json, typeof(TestEntity), typeof(TestDbContext), context);
        return (Func<TestEntity, bool>)lambda!.Compile();
    }

    // ══════════════════════════════════════════════════════════════
    //  12 Operator Tests (on string Name)
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_EqOperator_ReturnsCorrectLambda()
    {
        using var db = CreateDbContext();
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Name", Op = "eq", Value = "Alice" }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        func(new TestEntity { Name = "Alice" }).Should().BeTrue();
        func(new TestEntity { Name = "Bob" }).Should().BeFalse();
    }

    [Fact]
    public void Build_NeOperator_ReturnsCorrectLambda()
    {
        using var db = CreateDbContext();
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Name", Op = "ne", Value = "Alice" }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        func(new TestEntity { Name = "Bob" }).Should().BeTrue();
        func(new TestEntity { Name = "Alice" }).Should().BeFalse();
    }

    [Fact]
    public void Build_GtOperator_ReturnsCorrectLambda()
    {
        using var db = CreateDbContext();
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Age", Op = "gt", Value = "30" }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        func(new TestEntity { Age = 35 }).Should().BeTrue();
        func(new TestEntity { Age = 30 }).Should().BeFalse();
        func(new TestEntity { Age = 25 }).Should().BeFalse();
    }

    [Fact]
    public void Build_GteOperator_ReturnsCorrectLambda()
    {
        using var db = CreateDbContext();
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Age", Op = "gte", Value = "30" }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        func(new TestEntity { Age = 30 }).Should().BeTrue();
        func(new TestEntity { Age = 35 }).Should().BeTrue();
        func(new TestEntity { Age = 25 }).Should().BeFalse();
    }

    [Fact]
    public void Build_LtOperator_ReturnsCorrectLambda()
    {
        using var db = CreateDbContext();
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Age", Op = "lt", Value = "30" }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        func(new TestEntity { Age = 25 }).Should().BeTrue();
        func(new TestEntity { Age = 30 }).Should().BeFalse();
        func(new TestEntity { Age = 35 }).Should().BeFalse();
    }

    [Fact]
    public void Build_LteOperator_ReturnsCorrectLambda()
    {
        using var db = CreateDbContext();
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Age", Op = "lte", Value = "30" }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        func(new TestEntity { Age = 25 }).Should().BeTrue();
        func(new TestEntity { Age = 30 }).Should().BeTrue();
        func(new TestEntity { Age = 35 }).Should().BeFalse();
    }

    [Fact]
    public void Build_ContainsOperator_ReturnsCorrectLambda()
    {
        using var db = CreateDbContext();
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Name", Op = "contains", Value = "lic" }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        func(new TestEntity { Name = "Alice" }).Should().BeTrue();
        func(new TestEntity { Name = "Bob" }).Should().BeFalse();
    }

    [Fact]
    public void Build_StartsWithOperator_ReturnsCorrectLambda()
    {
        using var db = CreateDbContext();
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Name", Op = "startsWith", Value = "Al" }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        func(new TestEntity { Name = "Alice" }).Should().BeTrue();
        func(new TestEntity { Name = "Bob" }).Should().BeFalse();
    }

    [Fact]
    public void Build_EndsWithOperator_ReturnsCorrectLambda()
    {
        using var db = CreateDbContext();
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Name", Op = "endsWith", Value = "ice" }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        func(new TestEntity { Name = "Alice" }).Should().BeTrue();
        func(new TestEntity { Name = "Bob" }).Should().BeFalse();
    }

    [Fact]
    public void Build_InOperator_ReturnsCorrectLambda()
    {
        using var db = CreateDbContext();
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Name", Op = "in", Value = "Alice,Bob" }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        func(new TestEntity { Name = "Alice" }).Should().BeTrue();
        func(new TestEntity { Name = "Bob" }).Should().BeTrue();
        func(new TestEntity { Name = "Charlie" }).Should().BeFalse();
    }

    [Fact]
    public void Build_NotInOperator_ReturnsCorrectLambda()
    {
        using var db = CreateDbContext();
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Name", Op = "notIn", Value = "Alice,Bob" }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        func(new TestEntity { Name = "Charlie" }).Should().BeTrue();
        func(new TestEntity { Name = "Alice" }).Should().BeFalse();
        func(new TestEntity { Name = "Bob" }).Should().BeFalse();
    }

    // ══════════════════════════════════════════════════════════════
    //  BypassFilters Tests
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_BypassFiltersTrue_ReturnsAlwaysTrue()
    {
        using var db = CreateDbContext(bypassFilters: true);
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Name", Op = "eq", Value = "Alice" }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        // BypassFilters = true 使表达式短路为 true，无视过滤条件
        func(new TestEntity { Name = "Alice" }).Should().BeTrue();
        func(new TestEntity { Name = "Bob" }).Should().BeTrue();
        func(new TestEntity { Name = "Charlie" }).Should().BeTrue();
    }

    [Fact]
    public void Build_BypassFiltersFalse_FilterStillWorks()
    {
        using var db = CreateDbContext(bypassFilters: false);
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Name", Op = "eq", Value = "Alice" }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        // BypassFilters = false，过滤器正常工作
        func(new TestEntity { Name = "Alice" }).Should().BeTrue();
        func(new TestEntity { Name = "Bob" }).Should().BeFalse();
    }

    // ══════════════════════════════════════════════════════════════
    //  Placeholder Tests
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_LoginUserPlaceholder_ResolvesToCurrentUserCode()
    {
        using var db = CreateDbContext(userCode: "custom-user");
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Name", Op = "eq", Value = "{loginUser}" }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        func(new TestEntity { Name = "custom-user" }).Should().BeTrue();
        func(new TestEntity { Name = "other-user" }).Should().BeFalse();
    }

    [Fact]
    public void Build_LoginRolePlaceholder_ResolvesToCurrentRoleIds()
    {
        using var db = CreateDbContext(roleIds: new[] { "admin", "user" });
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Name", Op = "in", Value = "{loginRole}" }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        // {loginRole} 返回 CurrentRoleIds，in 操作符检查 Name 是否在角色列表中
        func(new TestEntity { Name = "admin" }).Should().BeTrue();
        func(new TestEntity { Name = "user" }).Should().BeTrue();
        func(new TestEntity { Name = "guest" }).Should().BeFalse();
    }

    [Fact]
    public void Build_LoginOrgPlaceholder_ResolvesToCurrentOrgIds()
    {
        using var db = CreateDbContext();
        // 默认 CurrentOrgIds 为空数组
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Name", Op = "in", Value = "{loginOrg}" }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        // CurrentOrgIds 默认 []，任何 Name 都不在空集合中 → 全部 false
        func(new TestEntity { Name = "org-a" }).Should().BeFalse();
    }

    // ══════════════════════════════════════════════════════════════
    //  Nested Group Tests
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_NestedAndGroup_CombinesCorrectly()
    {
        using var db = CreateDbContext();
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Name", Op = "eq", Value = "Alice" }],
            Children =
            [
                new ConditionRuleGroup
                {
                    Logic = "And",
                    Rules = [new ConditionRule { Field = "Age", Op = "gte", Value = "30" }]
                }
            ]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        // Name=Alice && Age>=30
        func(new TestEntity { Name = "Alice", Age = 30 }).Should().BeTrue();
        func(new TestEntity { Name = "Alice", Age = 25 }).Should().BeFalse();
        func(new TestEntity { Name = "Bob", Age = 30 }).Should().BeFalse();
    }

    [Fact]
    public void Build_NestedOrGroup_CombinesCorrectly()
    {
        using var db = CreateDbContext();
        var group = new ConditionRuleGroup
        {
            Logic = "Or",
            Rules = [new ConditionRule { Field = "Name", Op = "eq", Value = "Alice" }],
            Children =
            [
                new ConditionRuleGroup
                {
                    Logic = "And",
                    Rules = [new ConditionRule { Field = "Name", Op = "eq", Value = "Bob" }]
                }
            ]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        // Name=Alice OR Name=Bob
        func(new TestEntity { Name = "Alice" }).Should().BeTrue();
        func(new TestEntity { Name = "Bob" }).Should().BeTrue();
        func(new TestEntity { Name = "Charlie" }).Should().BeFalse();
    }

    [Fact]
    public void Build_MixedAndOrNestedGroup_CombinesCorrectly()
    {
        using var db = CreateDbContext();
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Age", Op = "gte", Value = "25" }],
            Children =
            [
                new ConditionRuleGroup
                {
                    Logic = "Or",
                    Rules =
                    [
                        new ConditionRule { Field = "Name", Op = "eq", Value = "Alice" },
                        new ConditionRule { Field = "Name", Op = "eq", Value = "Bob" }
                    ]
                }
            ]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        // Age>=25 AND (Name=Alice OR Name=Bob)
        func(new TestEntity { Name = "Alice", Age = 30 }).Should().BeTrue();
        func(new TestEntity { Name = "Bob", Age = 25 }).Should().BeTrue();
        func(new TestEntity { Name = "Alice", Age = 20 }).Should().BeFalse();
        func(new TestEntity { Name = "Charlie", Age = 30 }).Should().BeFalse();
    }

    // ══════════════════════════════════════════════════════════════
    //  Type Conversion Tests (one per property type)
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_IntPropertyEq_ConvertsAndCompares()
    {
        using var db = CreateDbContext();
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Age", Op = "eq", Value = "30" }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        func(new TestEntity { Age = 30 }).Should().BeTrue();
        func(new TestEntity { Age = 25 }).Should().BeFalse();
    }

    [Fact]
    public void Build_LongPropertyEq_ConvertsAndCompares()
    {
        using var db = CreateDbContext();
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Count", Op = "eq", Value = "1000" }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        func(new TestEntity { Count = 1000 }).Should().BeTrue();
        func(new TestEntity { Count = 999 }).Should().BeFalse();
    }

    [Fact]
    public void Build_GuidPropertyEq_ConvertsAndCompares()
    {
        using var db = CreateDbContext();
        var guid = Guid.NewGuid();
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "DepartmentId", Op = "eq", Value = guid.ToString() }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        func(new TestEntity { DepartmentId = guid }).Should().BeTrue();
        func(new TestEntity { DepartmentId = Guid.NewGuid() }).Should().BeFalse();
    }

    [Fact]
    public void Build_DateTimePropertyEq_ConvertsAndCompares()
    {
        using var db = CreateDbContext();
        var date = new DateTime(2025, 6, 1);
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Created", Op = "eq", Value = date.ToString("o") }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        func(new TestEntity { Created = date }).Should().BeTrue();
        func(new TestEntity { Created = date.AddDays(1) }).Should().BeFalse();
    }

    [Fact]
    public void Build_DateTimeOffsetPropertyEq_ConvertsAndCompares()
    {
        using var db = CreateDbContext();
        var dto = new DateTimeOffset(2025, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Modified", Op = "eq", Value = dto.ToString("o") }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        func(new TestEntity { Modified = dto }).Should().BeTrue();
        func(new TestEntity { Modified = dto.AddDays(1) }).Should().BeFalse();
    }

    [Fact]
    public void Build_BoolPropertyEq_ConvertsAndCompares()
    {
        using var db = CreateDbContext();
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Active", Op = "eq", Value = "True" }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        func(new TestEntity { Active = true }).Should().BeTrue();
        func(new TestEntity { Active = false }).Should().BeFalse();
    }

    [Fact]
    public void Build_DecimalPropertyEq_ConvertsAndCompares()
    {
        using var db = CreateDbContext();
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Price", Op = "eq", Value = "99.99" }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        func(new TestEntity { Price = 99.99m }).Should().BeTrue();
        func(new TestEntity { Price = 50.00m }).Should().BeFalse();
    }

    [Fact]
    public void Build_FloatPropertyEq_ConvertsAndCompares()
    {
        using var db = CreateDbContext();
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Rating", Op = "eq", Value = "4.5" }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        func(new TestEntity { Rating = 4.5f }).Should().BeTrue();
        func(new TestEntity { Rating = 3.0f }).Should().BeFalse();
    }

    [Fact]
    public void Build_DoublePropertyEq_ConvertsAndCompares()
    {
        using var db = CreateDbContext();
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Score", Op = "eq", Value = "88.5" }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        func(new TestEntity { Score = 88.5 }).Should().BeTrue();
        func(new TestEntity { Score = 75.0 }).Should().BeFalse();
    }

    [Fact]
    public void Build_EnumPropertyEq_ConvertsAndCompares()
    {
        using var db = CreateDbContext();
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Status", Op = "eq", Value = "Active" }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        func(new TestEntity { Status = StatusEnum.Active }).Should().BeTrue();
        func(new TestEntity { Status = StatusEnum.Inactive }).Should().BeFalse();
    }

    [Fact]
    public void Build_NullablePropertyEq_ConvertsAndCompares()
    {
        using var db = CreateDbContext();
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Description", Op = "eq", Value = "hello" }]
        };
        var json = SerializeGroup(group);
        var func = BuildAndCompile(json, db);

        func(new TestEntity { Description = "hello" }).Should().BeTrue();
        func(new TestEntity { Description = "world" }).Should().BeFalse();
        func(new TestEntity { Description = null }).Should().BeFalse();
    }

    // ══════════════════════════════════════════════════════════════
    //  Edge Case Tests
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_EmptyRuleJson_ReturnsNull()
    {
        using var db = CreateDbContext();
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [],
            Children = []
        };
        var json = SerializeGroup(group);
        var result = FilterExpressionBuilder.Build(
            json, typeof(TestEntity), typeof(TestDbContext), db);

        result.Should().BeNull();
    }

    [Fact]
    public void Build_NullRuleJson_ReturnsNull()
    {
        using var db = CreateDbContext();
        var result = FilterExpressionBuilder.Build(
            "null", typeof(TestEntity), typeof(TestDbContext), db);

        result.Should().BeNull();
    }

    [Fact]
    public void Build_MissingProperty_ThrowsInvalidOperationException()
    {
        using var db = CreateDbContext();
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "NonExistentProp", Op = "eq", Value = "x" }]
        };
        var json = SerializeGroup(group);

        var act = () => FilterExpressionBuilder.Build(
            json, typeof(TestEntity), typeof(TestDbContext), db);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*NonExistentProp*");
    }

    [Fact]
    public void Build_UnknownOperator_ThrowsNotSupportedException()
    {
        using var db = CreateDbContext();
        var group = new ConditionRuleGroup
        {
            Logic = "And",
            Rules = [new ConditionRule { Field = "Name", Op = "unknownOp", Value = "x" }]
        };
        var json = SerializeGroup(group);

        var act = () => FilterExpressionBuilder.Build(
            json, typeof(TestEntity), typeof(TestDbContext), db);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*unknownOp*");
    }
}
