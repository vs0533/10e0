using Microsoft.EntityFrameworkCore;
using TenE0.Core.Auth.Jwt.Storage;
using TenE0.Core.Organizations;
using TenE0.Core.Workflow.Definitions;

namespace TenE0.Api.Hosting;

/// <summary>
/// <see cref="IAssigneeDirectory"/> 的 Api 层实现 — 查 EF Core 的 TenE0UserRole + 组织树。
///
/// 把"角色/组织 → 用户编码"的查询从 Core 的 Resolver 解耦：Core 的 Resolver 只依赖
/// <see cref="IAssigneeDirectory"/> 抽象，具体的数据访问（EF 表 / 外部 IdP）由宿主层提供。
/// </summary>
public sealed class AssigneeDirectory<TContext>(
    IDbContextFactory<TContext> contextFactory,
    IOrgTreeService orgTreeService) : IAssigneeDirectory
    where TContext : DbContext
{
    public async Task<IReadOnlyList<string>> GetUsersByRoleAsync(string roleCode, CancellationToken ct = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);
        var users = await dc.Set<TenE0UserRole>()
            .Where(ur => ur.RoleCode == roleCode && !ur.IsSoftDelete)
            .Select(ur => ur.UserCode)
            .ToListAsync(ct);
        return users;
    }

    public async Task<string?> GetManagerOrgIdAsync(string orgId, int level, CancellationToken ct = default)
    {
        // 取祖先（根 → 父，不含自身），按层级从近到远。level=1 取最近的上级组织。
        var ancestors = await orgTreeService.GetAncestorsAsync(orgId, ct);
        if (ancestors.Count == 0) return null;
        // 祖先是"父→祖父→...→根"顺序（近到远），取第 level 个
        // 数组索引：level=1 → index 0（直接父级）
        var idx = level - 1;
        if (idx >= ancestors.Count) return ancestors[^1].Id; // 超过则取最远的（根）
        return ancestors[idx].Id;
    }

    public async Task<IReadOnlyList<string>> GetOrgMembersAsync(string orgId, CancellationToken ct = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);
        // Demo 简化：业务用户实体（AppUser）扩展了 Department 字段，按 Department 近似匹配组织。
        // 生产实现应让 AppUser 增加 OrgId 字段并按强类型属性过滤：
        //   .Where(u => u.OrgId == orgId)
        var users = await dc.Set<TenE0User>()
            .Where(u => !u.IsSoftDelete && EF.Property<string?>(u, "Department") == orgId)
            .Select(u => u.UserCode)
            .ToListAsync(ct);
        return users;
    }
}
