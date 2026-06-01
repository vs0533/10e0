using Microsoft.EntityFrameworkCore;
using TenE0.Core.Permissions.Storage;

namespace TenE0.Core.Permissions.Management;

/// <summary>
/// PermissionGrantService 的 EF 实现。
/// 不再依赖 IPermissionDataContext —— 用 ctx.Set&lt;TenE0RolePermission&gt;() 访问表，
/// TContext 只需是 DbContext + 在 model 中注册了该实体（TenE0SystemDbContext 已自动做）。
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
        if (exists) return; // 幂等

        ctx.Set<TenE0RolePermission>().Add(new TenE0RolePermission { RoleCode = roleCode, PermissionKey = permissionKey });
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

        if (rows.Count == 0) return;

        ctx.Set<TenE0RolePermission>().RemoveRange(rows);
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
        var currentKeys = current.Select(rp => rp.PermissionKey).ToHashSet();

        set.RemoveRange(current.Where(rp => !target.Contains(rp.PermissionKey)));
        set.AddRange(target.Where(k => !currentKeys.Contains(k))
            .Select(k => new TenE0RolePermission { RoleCode = roleCode, PermissionKey = k }));

        await ctx.SaveChangesAsync(cancellationToken);
        await cache.InvalidateRoleAsync(roleCode, cancellationToken);
    }

    private void EnsureKeyDefined(string permissionKey)
    {
        if (!catalog.Contains(permissionKey))
            throw new InvalidOperationException(
                $"权限 key '{permissionKey}' 未在 PermissionCatalog 中定义。" +
                $"请在 IPermissionProvider 中声明后再 grant。");
    }
}
