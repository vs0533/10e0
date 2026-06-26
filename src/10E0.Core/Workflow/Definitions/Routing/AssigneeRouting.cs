namespace TenE0.Core.Workflow.Definitions;

/// <summary>审批人解析策略种类。</summary>
public enum AssigneePolicyKind
{
    /// <summary>按角色取该角色所有用户。</summary>
    Role,
    /// <summary>直接上级（直属上级）。</summary>
    Manager,
    /// <summary>N 级上级（向上数 N 级）。</summary>
    NLevelManager,
    /// <summary>指定用户列表。</summary>
    User,
    /// <summary>表达式（如 "ctx.Initiator.Manager"）。</summary>
    Expression,
}

/// <summary>
/// 审批人解析策略 — 声明"该节点的审批人从哪来"。
/// 解析由 <see cref="IAssigneeResolver"/>（按 <see cref="Kind"/> 匹配）执行。
/// </summary>
public sealed record AssigneePolicy
{
    /// <summary>策略种类。</summary>
    public required AssigneePolicyKind Kind { get; init; }

    /// <summary>角色编码（<see cref="AssigneePolicyKind.Role"/> 时必填）。</summary>
    public string? RoleCode { get; init; }

    /// <summary>上级层级（<see cref="AssigneePolicyKind.Manager"/> 默认 1，<see cref="AssigneePolicyKind.NLevelManager"/> 时为向上数 N 级）。</summary>
    public int ManagerLevel { get; init; } = 1;

    /// <summary>用户编码列表（<see cref="AssigneePolicyKind.User"/> 时必填）。</summary>
    public IReadOnlyList<string>? UserCodes { get; init; }

    /// <summary>表达式（<see cref="AssigneePolicyKind.Expression"/> 时必填）。</summary>
    public string? Expression { get; init; }

    // ---- 工厂方法（fluent builder 用）----

    public static AssigneePolicy Role(string roleCode) => new() { Kind = AssigneePolicyKind.Role, RoleCode = roleCode };
    public static AssigneePolicy Manager(int level = 1) => new() { Kind = AssigneePolicyKind.Manager, ManagerLevel = level };
    public static AssigneePolicy NLevelManager(int level) => new() { Kind = AssigneePolicyKind.NLevelManager, ManagerLevel = level };
    public static AssigneePolicy User(params string[] userCodes) => new() { Kind = AssigneePolicyKind.User, UserCodes = userCodes };
    public static AssigneePolicy FromExpression(string expression) => new() { Kind = AssigneePolicyKind.Expression, Expression = expression };
}

/// <summary>
/// 审批人解析上下文 — 携带发起人 / 租户 / 业务数据。
/// 业务数据字典供分支条件求值与表达式解析使用。
/// </summary>
public sealed record ResolveContext
{
    /// <summary>流程发起人（用户编码）。</summary>
    public required string Initiator { get; init; }

    /// <summary>发起人所属组织 ID（用于上级解析）。</summary>
    public string? InitiatorOrgId { get; init; }

    /// <summary>租户 ID。</summary>
    public string? TenantId { get; init; }

    /// <summary>业务数据（字段名 → 值），供条件求值 / 表达式解析。</summary>
    public IReadOnlyDictionary<string, object?> BusinessData { get; init; } = new Dictionary<string, object?>();
}

/// <summary>
/// 审批人目录 — 把"角色/组织 → 用户编码"的查询从 Resolver 解耦。
///
/// 为什么需要它：<see cref="RoleAssigneeResolver"/> / <see cref="ManagerAssigneeResolver"/>
/// 需要查"某角色的所有用户"/"某人的上级"，这些数据在 EF Core 表里（TenE0UserRole / TenE0Org），
/// 但 Core 的 Resolver 不应直接依赖具体 DbContext。故定义此抽象，由 Api 层实现。
/// </summary>
public interface IAssigneeDirectory
{
    /// <summary>取某角色的所有用户编码。</summary>
    Task<IReadOnlyList<string>> GetUsersByRoleAsync(string roleCode, CancellationToken ct = default);

    /// <summary>取指定组织节点的直接上级组织节点（同级或父级）。</summary>
    Task<string?> GetManagerOrgIdAsync(string orgId, int level, CancellationToken ct = default);

    /// <summary>取某组织的所有成员用户编码。</summary>
    Task<IReadOnlyList<string>> GetOrgMembersAsync(string orgId, CancellationToken ct = default);
}

/// <summary>审批人解析器抽象 — 按 <see cref="PolicyName"/> 匹配策略种类。</summary>
public interface IAssigneeResolver
{
    /// <summary>本 Resolver 处理的策略种类。</summary>
    AssigneePolicyKind PolicyName { get; }

    /// <summary>解析该节点审批人列表。</summary>
    Task<IReadOnlyList<string>> ResolveAsync(AssigneePolicy policy, ResolveContext ctx, CancellationToken ct = default);
}
