using Microsoft.EntityFrameworkCore;

namespace TenE0.Core.Permissions.Storage;

/// <summary>
/// IPermissionStore 的 EF Core 实现。
///
/// 不再依赖 IPermissionDataContext 接口 — TContext 只需是 DbContext，
/// 框架表（TenE0RolePermission）由 TenE0SystemDbContext 基类自动注册到 model。
/// </summary>
public sealed class EfPermissionStore<TContext>(IDbContextFactory<TContext> contextFactory)
    : IPermissionStore where TContext : DbContext
{
    public async Task<IReadOnlySet<string>> GetGrantedPermissionsAsync(
        IReadOnlyCollection<string> roleIds,
        CancellationToken cancellationToken = default)
    {
        if (roleIds.Count == 0) return new HashSet<string>();

        await using var ctx = await contextFactory.CreateDbContextAsync(cancellationToken);
        var perms = await ctx.Set<TenE0RolePermission>()
            .AsNoTracking()
            .Where(rp => roleIds.Contains(rp.RoleCode))
            .Select(rp => rp.PermissionKey)
            .Distinct()
            .ToListAsync(cancellationToken);

        return perms.ToHashSet();
    }
}
