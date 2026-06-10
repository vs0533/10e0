namespace TenE0.Core.Permissions;

/// <summary>
/// 角色版本号存储 — 用于 #7 "instant permission revocation"。
///
/// 每次 <see cref="IPermissionGrantService"/> 实际变更某角色的权限授予（grant / revoke /
/// setGrants）时，框架会 bump 该角色的 <c>TenE0Role.Version</c> 计数。新签发的 JWT 会把
/// 当下所有版本的快照嵌进 <c>role_versions</c> claim（<see cref="Abstractions.JwtClaims.RoleVersion"/>）。
///
/// 每次 <see cref="IPermissionEvaluator.HasAsync"/> 都会把这个 claim 里的快照和 store
/// 里的最新值比较：任一角色 db version &gt; token version → 立即视为权限被撤销，
/// 主动调 <see cref="IPermissionCache.InvalidateRoleAsync"/> 让后续请求走 store 重读。
///
/// 实现层应提供 L1（IMemoryCache）以满足 &lt; 5ms / HasAsync 的性能目标。
/// </summary>
public interface IRoleVersionStore
{
    /// <summary>
    /// 取出指定角色集合当前的版本号。未匹配到的角色不会出现在结果字典里（视为 0）。
    /// </summary>
    Task<IReadOnlyDictionary<string, long>> GetCurrentVersionsAsync(
        IReadOnlyCollection<string> roleCodes,
        CancellationToken cancellationToken = default);
}
