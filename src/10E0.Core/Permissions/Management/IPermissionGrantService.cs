namespace TenE0.Core.Permissions.Management;

/// <summary>
/// 权限管理服务 — grant / revoke 操作，写 DB 后失效缓存。
///
/// 设计原则：
/// - 所有写操作都校验 PermissionKey 在 PermissionCatalog 中存在（避免脏数据）
/// - 写完立即调 IPermissionCache.InvalidateRoleAsync，下一次评估强制重读
/// - 不内置 Admin API（具体 endpoint 由调用方在 Program.cs 中拼装），保持框架轻量
/// </summary>
public interface IPermissionGrantService
{
    Task<IReadOnlyList<string>> ListGrantedAsync(string roleCode, CancellationToken cancellationToken = default);

    Task GrantAsync(string roleCode, string permissionKey, CancellationToken cancellationToken = default);

    Task RevokeAsync(string roleCode, string permissionKey, CancellationToken cancellationToken = default);

    /// <summary>批量替换某角色的全部权限（用于"保存权限设置"场景）。</summary>
    Task SetGrantsAsync(string roleCode, IEnumerable<string> permissionKeys, CancellationToken cancellationToken = default);
}
