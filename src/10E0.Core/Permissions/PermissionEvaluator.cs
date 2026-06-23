using Microsoft.Extensions.Options;
using TenE0.Core.Abstractions;

namespace TenE0.Core.Permissions;

/// <summary>
/// IPermissionEvaluator 默认实现。
///
/// 升级要点（vs phase 3.1）：
/// - 缓存粒度按角色（IPermissionCache）— grant/revoke 时精准失效该角色
/// - 用户的最终权限 = 其所有角色权限的并集，每角色独立缓存
/// - 超管判断改为 SuperUserRoles（基于 JWT role claim），更符合 RBAC
///
/// #7 增量：role version 检查（instant permission revocation）
/// - 在聚合前对比 token 携带的 role_versions vs DB 当前 version
/// - 任一角色 db_version &gt; token_version → 调 IPermissionCache.InvalidateRoleAsync + 绕过该角色 cache
///   直接走 IPermissionStore 重读。这样：
///     * revoke 后 store 不含该权限 → 下次 HasAsync 自然 false
///     * grant 后 store 含该权限 → 下次 HasAsync 自然 true
/// - token 中没有 role_versions claim（legacy token）→ 放行，不查 version store
/// - super_user 短路 + 未认证短路都在 version check 之前
/// </summary>
internal sealed class PermissionEvaluator(
    ICurrentUserContext currentUser,
    IPermissionStore store,
    IPermissionCache cache,
    IRoleVersionStore roleVersions,
    IOptions<PermissionsOptions> options) : IPermissionEvaluator
{
    private readonly PermissionsOptions _options = options.Value;

    /// <summary>#106: 静态空集合，避免 store 违约 null 时每次分配新 HashSet 兜底。</summary>
    private static readonly IReadOnlySet<string> EmptySet =
        new HashSet<string>(0, StringComparer.Ordinal);

    public async Task<bool> HasAsync(string permissionKey, CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated) return false;
        if (IsSuperUser()) return true;
        var stale = await DetectStaleRolesAsync(cancellationToken);

        var granted = await GetGrantedAsync(stale, cancellationToken);
        return granted.Contains(permissionKey);
    }

    public async Task<bool> HasAnyAsync(IEnumerable<string> permissionKeys, CancellationToken cancellationToken = default)
    {
        var list = permissionKeys.ToList();
        if (list.Count == 0) return true;
        if (!currentUser.IsAuthenticated) return false;
        if (IsSuperUser()) return true;
        var stale = await DetectStaleRolesAsync(cancellationToken);

        var granted = await GetGrantedAsync(stale, cancellationToken);
        return list.Any(granted.Contains);
    }

    public async Task<bool> HasAllAsync(IEnumerable<string> permissionKeys, CancellationToken cancellationToken = default)
    {
        var list = permissionKeys.ToList();
        if (list.Count == 0) return true;
        if (!currentUser.IsAuthenticated) return false;
        if (IsSuperUser()) return true;
        var stale = await DetectStaleRolesAsync(cancellationToken);

        var granted = await GetGrantedAsync(stale, cancellationToken);
        return list.All(granted.Contains);
    }

    /// <summary>当前用户的任一角色属于 SuperUserRoles 即视为超管。</summary>
    private bool IsSuperUser()
        => currentUser.RoleIds.Any(r => _options.SuperUserRoles.Contains(r));

    /// <summary>
    /// #7 增量：检测 token 中的 role_versions 是否落后于 DB。
    /// 返回本调用周期内被检测为 stale 的角色集合。stale 角色在同周期的
    /// <see cref="GetGrantedAsync"/> 中会绕过 cache 直接走 store。
    /// 副作用：每个 stale role 还会调 <see cref="IPermissionCache.InvalidateRoleAsync"/>。
    /// 空 RoleVersions 视为 legacy token，返回空 set。
    /// </summary>
    private async Task<HashSet<string>> DetectStaleRolesAsync(CancellationToken cancellationToken)
    {
        var stale = new HashSet<string>(StringComparer.Ordinal);
        var tokenVersions = currentUser.RoleVersions;
        if (tokenVersions.Count == 0) return stale;

        var distinctRoles = currentUser.RoleIds.Distinct(StringComparer.Ordinal).ToList();
        if (distinctRoles.Count == 0) return stale;

        var current = await roleVersions.GetCurrentVersionsAsync(distinctRoles, cancellationToken);

        foreach (var role in distinctRoles)
        {
            var tokenV = tokenVersions.TryGetValue(role, out var tv) ? tv : 0L;
            if (current.TryGetValue(role, out var dbV) && dbV > tokenV)
            {
                stale.Add(role);
                await cache.InvalidateRoleAsync(role, cancellationToken);
            }
        }

        return stale;
    }

    /// <summary>聚合当前用户所有角色的权限快照（按角色 cache，逐个合并）。</summary>
    private async Task<HashSet<string>> GetGrantedAsync(HashSet<string> staleRoles, CancellationToken cancellationToken)
    {
        var union = new HashSet<string>(StringComparer.Ordinal);

        foreach (var roleCode in currentUser.RoleIds.Distinct(StringComparer.Ordinal))
        {
            IReadOnlySet<string>? rolePerms;
            if (staleRoles.Contains(roleCode))
            {
                // 强制走 store（绕过被失效的 cache）— 真实反映最新授权
                rolePerms = await store.GetGrantedPermissionsAsync([roleCode], cancellationToken);
            }
            else
            {
                rolePerms = await cache.GetRolePermissionsAsync(roleCode, cancellationToken);
                if (rolePerms is null)
                {
                    rolePerms = await store.GetGrantedPermissionsAsync([roleCode], cancellationToken);
                    await cache.SetRolePermissionsAsync(roleCode, rolePerms, cancellationToken);
                }
            }
            // Store 契约保证 non-null（IPermissionStore.GetGrantedPermissionsAsync 文档明确）。
            // #106: 旧代码每次分配 new HashSet 兜底；改用静态空集合避免热路径分配。
            // 若 store 实现意外返回 null（契约违反），降级为空集 —— revoke 路径仍正确（union 不含该 key）。
            union.UnionWith(rolePerms ?? EmptySet);
        }

        return union;
    }
}

/// <summary>权限模块配置选项。</summary>
public sealed class PermissionsOptions
{
    /// <summary>
    /// 超管角色集合（任一匹配即视为超管）。默认空集合 — 不开启超管。
    /// 例：opt.SuperUserRoles.Add("super_admin")
    /// </summary>
    public HashSet<string> SuperUserRoles { get; init; } = new(StringComparer.Ordinal);

    /// <summary>权限快照缓存时长。</summary>
    public TimeSpan CacheDuration { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// IDataAccessPolicy 的权限模块实现：当前用户拥有任一 SuperUserRoles 即 bypass 所有行过滤。
/// </summary>
internal sealed class SuperUserDataAccessPolicy(
    ICurrentUserContext currentUser,
    IOptions<PermissionsOptions> options) : IDataAccessPolicy
{
    public bool BypassFilters =>
        currentUser.IsAuthenticated &&
        currentUser.RoleIds.Any(r => options.Value.SuperUserRoles.Contains(r));
}
