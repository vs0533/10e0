namespace TenE0.Core.Permissions;

/// <summary>
/// 权限缓存抽象 — 按角色粒度缓存权限快照，支持精准失效。
///
/// 设计理由（替代 phase 3.1 的"按用户缓存"方案）：
/// - 角色变更（grant/revoke）影响所有持有该角色的用户，但我们不可能枚举所有在线用户
/// - 按角色缓存只需失效"该角色"的一个 key，影响精准
/// - 用户级权限 = 其所有角色权限的并集（在 PermissionEvaluator 中合并）
/// </summary>
public interface IPermissionCache
{
    /// <summary>取指定角色已授予权限的快照；未命中返回 null。</summary>
    Task<IReadOnlySet<string>?> GetRolePermissionsAsync(string roleCode, CancellationToken cancellationToken = default);

    /// <summary>写入指定角色的权限快照。TTL 由实现决定。</summary>
    Task SetRolePermissionsAsync(string roleCode, IReadOnlySet<string> permissions, CancellationToken cancellationToken = default);

    /// <summary>失效指定角色的缓存（grant/revoke 时调用）。</summary>
    Task InvalidateRoleAsync(string roleCode, CancellationToken cancellationToken = default);

    /// <summary>失效全部角色缓存（罕用，例如导入后清空）。实现可选。</summary>
    Task InvalidateAllAsync(CancellationToken cancellationToken = default);
}
