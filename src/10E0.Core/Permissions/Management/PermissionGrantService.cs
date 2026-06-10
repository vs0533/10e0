using Microsoft.EntityFrameworkCore;
using TenE0.Core.Permissions.Storage;

namespace TenE0.Core.Permissions.Management;

/// <summary>
/// PermissionGrantService 的 EF 实现。
/// 不再依赖 IPermissionDataContext —— 用 ctx.Set&lt;TenE0RolePermission&gt;() 访问表，
/// TContext 只需是 DbContext + 在 model 中注册了该实体（TenE0SystemDbContext 已自动做）。
///
/// #7 增量：每次实际 grant / revoke / setGrants 都会 bump 对应 <see cref="TenE0Role.Version"/>，
/// 让 <see cref="IPermissionEvaluator"/> 能在 token 已签发的情况下立刻检测到权限变更。
/// 幂等操作（重复 grant、revoke 不存在的 grant、setGrants 与现状一致）不会 bump — 避免无意义的缓存失效。
/// </summary>
public sealed class PermissionGrantService<TContext>(
    IDbContextFactory<TContext> contextFactory,
    PermissionCatalog catalog,
    IPermissionCache cache) : IPermissionGrantService
    where TContext : DbContext
{
    public async Task<IReadOnlyList<string>> ListGrantedAsync(string roleCode, CancellationToken cancellationToken = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await ctx.Set<TenE0RolePermission>()
            .AsNoTracking()
            .Where(rp => rp.RoleCode == roleCode)
            .Select(rp => rp.PermissionKey)
            .OrderBy(k => k)
            .ToListAsync(cancellationToken);
    }

    public async Task GrantAsync(string roleCode, string permissionKey, CancellationToken cancellationToken = default)
    {
        EnsureKeyDefined(permissionKey);

        await using var ctx = await contextFactory.CreateDbContextAsync(cancellationToken);

        var exists = await ctx.Set<TenE0RolePermission>()
            .AnyAsync(rp => rp.RoleCode == roleCode && rp.PermissionKey == permissionKey, cancellationToken);
        if (exists) return; // 幂等：不 bump、不失效

        ctx.Set<TenE0RolePermission>().Add(new TenE0RolePermission { RoleCode = roleCode, PermissionKey = permissionKey });
        await BumpVersionAsync(ctx, roleCode, cancellationToken);
        await ctx.SaveChangesAsync(cancellationToken);
        await cache.InvalidateRoleAsync(roleCode, cancellationToken);
    }

    public async Task RevokeAsync(string roleCode, string permissionKey, CancellationToken cancellationToken = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(cancellationToken);

        // 不用 ExecuteDelete（InMemory provider 不支持，且本操作低频）
        var rows = await ctx.Set<TenE0RolePermission>()
            .Where(rp => rp.RoleCode == roleCode && rp.PermissionKey == permissionKey)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0) return; // 幂等：不 bump、不失效

        ctx.Set<TenE0RolePermission>().RemoveRange(rows);
        await BumpVersionAsync(ctx, roleCode, cancellationToken);
        await ctx.SaveChangesAsync(cancellationToken);
        await cache.InvalidateRoleAsync(roleCode, cancellationToken);
    }

    public async Task SetGrantsAsync(string roleCode, IEnumerable<string> permissionKeys, CancellationToken cancellationToken = default)
    {
        var target = new HashSet<string>(permissionKeys);
        foreach (var key in target) EnsureKeyDefined(key);

        await using var ctx = await contextFactory.CreateDbContextAsync(cancellationToken);
        var set = ctx.Set<TenE0RolePermission>();

        var current = await set.Where(rp => rp.RoleCode == roleCode).ToListAsync(cancellationToken);
        var currentKeys = current.Select(rp => rp.PermissionKey).ToHashSet(StringComparer.Ordinal);

        // 没有任何差异 → 幂等 no-op
        if (currentKeys.SetEquals(target)) return;

        set.RemoveRange(current.Where(rp => !target.Contains(rp.PermissionKey)));
        set.AddRange(target.Where(k => !currentKeys.Contains(k))
            .Select(k => new TenE0RolePermission { RoleCode = roleCode, PermissionKey = k }));

        await BumpVersionAsync(ctx, roleCode, cancellationToken);
        await ctx.SaveChangesAsync(cancellationToken);
        await cache.InvalidateRoleAsync(roleCode, cancellationToken);
    }

    /// <summary>
    /// 自增 <see cref="TenE0Role.Version"/>。
    /// 先 SaveChanges（把 grant 变更落库）再 bump 是为了：1) 真实变更后立即触发，2) 与 SaveChanges 共享
    /// 同一事务（SaveChanges 落库时版本号随 grant 一起原子提交）。
    /// </summary>
    private static async Task BumpVersionAsync(TContext ctx, string roleCode, CancellationToken cancellationToken)
    {
        var role = await ctx.Set<TenE0Role>()
            .FirstOrDefaultAsync(r => r.Code == roleCode, cancellationToken);
        if (role is null)
        {
            // 角色不存在 → 走的是非常规路径。常见情况：seed 漏了或外部直插 TenE0RolePermission。
            // 直接抛错比静默成功更安全 — 管理员需要感知。
            throw new InvalidOperationException(
                $"Cannot bump version for role '{roleCode}': role not found in TenE0Role.");
        }
        role.Version += 1L;
    }

    private void EnsureKeyDefined(string permissionKey)
    {
        if (!catalog.Contains(permissionKey))
            throw new InvalidOperationException(
                $"权限 key '{permissionKey}' 未在 PermissionCatalog 中定义。" +
                $"请在 IPermissionProvider 中声明后再 grant。");
    }
}
