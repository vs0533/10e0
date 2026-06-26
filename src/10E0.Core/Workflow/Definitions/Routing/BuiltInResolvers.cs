namespace TenE0.Core.Workflow.Definitions;

/// <summary>
/// 按角色解析审批人 — 走 <see cref="IAssigneeDirectory.GetUsersByRoleAsync"/>。
/// </summary>
public sealed class RoleAssigneeResolver(IAssigneeDirectory directory) : IAssigneeResolver
{
    public AssigneePolicyKind PolicyName => AssigneePolicyKind.Role;

    public async Task<IReadOnlyList<string>> ResolveAsync(
        AssigneePolicy policy, ResolveContext ctx, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(policy.RoleCode))
            throw new InvalidOperationException("RoleAssigneeResolver 要求 AssigneePolicy.RoleCode 非空");
        return await directory.GetUsersByRoleAsync(policy.RoleCode, ct);
    }
}

/// <summary>
/// 按直接/N 级上级解析审批人 — 走 <see cref="IAssigneeDirectory"/>（由 Api 层实现，内部用
/// <see cref="IOrgTreeService"/> 取祖先链定位上级组织，再取成员）。
/// </summary>
public sealed class ManagerAssigneeResolver(IAssigneeDirectory directory) : IAssigneeResolver
{
    public AssigneePolicyKind PolicyName => AssigneePolicyKind.Manager;

    public async Task<IReadOnlyList<string>> ResolveAsync(
        AssigneePolicy policy, ResolveContext ctx, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ctx.InitiatorOrgId))
            return [];

        // NLevelManager 向上数 N 级；Manager 默认 1 级
        var level = policy.Kind == AssigneePolicyKind.NLevelManager
            ? policy.ManagerLevel
            : 1;

        var managerOrgId = await directory.GetManagerOrgIdAsync(ctx.InitiatorOrgId, level, ct);
        if (managerOrgId is null) return [];

        return await directory.GetOrgMembersAsync(managerOrgId, ct);
    }
}

/// <summary>
/// 指定人审批 — 直接返回 <see cref="AssigneePolicy.UserCodes"/>。
/// </summary>
public sealed class UserAssigneeResolver : IAssigneeResolver
{
    public AssigneePolicyKind PolicyName => AssigneePolicyKind.User;

    public Task<IReadOnlyList<string>> ResolveAsync(
        AssigneePolicy policy, ResolveContext ctx, CancellationToken ct = default)
    {
        IReadOnlyList<string> result = policy.UserCodes ?? [];
        return Task.FromResult(result);
    }
}

/// <summary>
/// 表达式解析审批人 — 解析 <see cref="AssigneePolicy.Expression"/>（如 "ctx.Initiator"）。
///
/// 本期实现轻量：仅支持占位符替换（"initiator" → 发起人；"initiator.org.members" → 发起人组织成员）。
/// 完整 Dynamic.Linq.Core 表达式求值作为后续增强（issue 原文标注可选）。
/// </summary>
public sealed class ExpressionAssigneeResolver(IAssigneeDirectory directory) : IAssigneeResolver
{
    public AssigneePolicyKind PolicyName => AssigneePolicyKind.Expression;

    public async Task<IReadOnlyList<string>> ResolveAsync(
        AssigneePolicy policy, ResolveContext ctx, CancellationToken ct = default)
    {
        var expr = policy.Expression?.Trim();
        if (string.IsNullOrEmpty(expr)) return [];

        // 轻量占位符语义
        return expr switch
        {
            "initiator" => [ctx.Initiator],
            "initiator.org.members" when ctx.InitiatorOrgId is not null
                => await directory.GetOrgMembersAsync(ctx.InitiatorOrgId, ct),
            _ => throw new InvalidOperationException(
                $"ExpressionAssigneeResolver 暂不支持表达式 '{expr}'。本期仅支持 'initiator' / 'initiator.org.members'。"),
        };
    }
}
