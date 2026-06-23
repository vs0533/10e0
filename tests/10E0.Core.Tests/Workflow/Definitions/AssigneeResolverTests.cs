using TenE0.Core.Workflow.Definitions;

namespace TenE0.Core.Tests.Workflow.Definitions;

/// <summary>
/// #158 AssigneeResolver 测试 — 内置 4 个 resolver 的解析逻辑。
/// </summary>
[Trait("Category", "Unit")]
public sealed class AssigneeResolverTests
{
    /// <summary>内存版 IAssigneeDirectory 实现（测试用）。</summary>
    private sealed class FakeDirectory : IAssigneeDirectory
    {
        private readonly Dictionary<string, List<string>> _roleUsers = [];
        private readonly Dictionary<string, List<string>> _orgMembers = [];

        public void AddRoleUser(string role, string user) =>
            (_roleUsers.TryGetValue(role, out var l) ? l : (_roleUsers[role] = [])).Add(user);

        public void SetOrgMembers(string org, params string[] users) => _orgMembers[org] = [.. users];
        public string? ManagerOrg { get; set; }

        public Task<IReadOnlyList<string>> GetUsersByRoleAsync(string roleCode, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(_roleUsers.GetValueOrDefault(roleCode) ?? []);

        public Task<string?> GetManagerOrgIdAsync(string orgId, int level, CancellationToken ct = default)
            => Task.FromResult(ManagerOrg);

        public Task<IReadOnlyList<string>> GetOrgMembersAsync(string orgId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(_orgMembers.GetValueOrDefault(orgId) ?? []);
    }

    private static ResolveContext Ctx(string initiator = "u1", string? orgId = "org1")
        => new() { Initiator = initiator, InitiatorOrgId = orgId, TenantId = "t1" };

    [Fact]
    public async Task RoleResolver_ReturnsUsersForRole()
    {
        var dir = new FakeDirectory();
        dir.AddRoleUser("director", "u10");
        dir.AddRoleUser("director", "u11");
        var resolver = new RoleAssigneeResolver(dir);

        var users = await resolver.ResolveAsync(AssigneePolicy.Role("director"), Ctx());

        users.Should().BeEquivalentTo(["u10", "u11"]);
    }

    [Fact]
    public async Task RoleResolver_NullRoleCode_Throws()
    {
        var resolver = new RoleAssigneeResolver(new FakeDirectory());

        var act = () => resolver.ResolveAsync(AssigneePolicy.Role(null!), Ctx());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UserResolver_ReturnsLiteralUserCodes()
    {
        var resolver = new UserAssigneeResolver();

        var users = await resolver.ResolveAsync(AssigneePolicy.User("u1", "u2"), Ctx());

        users.Should().BeEquivalentTo(["u1", "u2"]);
    }

    [Fact]
    public async Task ManagerResolver_ReturnsManagerOrgMembers()
    {
        var dir = new FakeDirectory { ManagerOrg = "parentOrg" };
        dir.SetOrgMembers("parentOrg", "boss1", "boss2");
        var resolver = new ManagerAssigneeResolver(dir);

        var users = await resolver.ResolveAsync(AssigneePolicy.Manager(), Ctx());

        users.Should().BeEquivalentTo(["boss1", "boss2"]);
    }

    [Fact]
    public async Task ManagerResolver_NoInitiatorOrg_ReturnsEmpty()
    {
        var dir = new FakeDirectory { ManagerOrg = "parentOrg" };
        var resolver = new ManagerAssigneeResolver(dir);

        var users = await resolver.ResolveAsync(AssigneePolicy.Manager(), Ctx(orgId: null));

        users.Should().BeEmpty();
    }

    [Fact]
    public async Task ManagerResolver_NoManagerOrg_ReturnsEmpty()
    {
        var dir = new FakeDirectory { ManagerOrg = null };
        var resolver = new ManagerAssigneeResolver(dir);

        var users = await resolver.ResolveAsync(AssigneePolicy.Manager(), Ctx());

        users.Should().BeEmpty();
    }

    [Fact]
    public async Task ExpressionResolver_InitiatorPlaceholder_ReturnsInitiator()
    {
        var resolver = new ExpressionAssigneeResolver(new FakeDirectory());

        var users = await resolver.ResolveAsync(AssigneePolicy.FromExpression("initiator"), Ctx(initiator: "u777"));

        users.Should().BeEquivalentTo(["u777"]);
    }

    [Fact]
    public async Task ExpressionResolver_UnsupportedExpression_Throws()
    {
        var resolver = new ExpressionAssigneeResolver(new FakeDirectory());

        var act = () => resolver.ResolveAsync(AssigneePolicy.FromExpression("unknown.expr"), Ctx());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
