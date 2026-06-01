namespace TenE0.Core.Permissions;

/// <summary>
/// 当前请求上下文中的权限评估器。
///
/// 替代旧 IPrivilege.GetAccessCode(tag) — 名称更准（evaluator 表达"判断"语义，旧 IPrivilege 既是定义又是判断混在一起）。
///
/// 实现层封装：当前用户 + 角色 → 缓存 → IPermissionStore 查询 → 决策。
/// </summary>
public interface IPermissionEvaluator
{
    /// <summary>当前用户是否有指定权限。未登录用户始终 false（admin 短路除外，由实现决定）。</summary>
    Task<bool> HasAsync(string permissionKey, CancellationToken cancellationToken = default);

    /// <summary>任一权限满足即返回 true（OR）。空集合返回 true（视为无要求）。</summary>
    Task<bool> HasAnyAsync(IEnumerable<string> permissionKeys, CancellationToken cancellationToken = default);

    /// <summary>所有权限都满足才返回 true（AND）。空集合返回 true。</summary>
    Task<bool> HasAllAsync(IEnumerable<string> permissionKeys, CancellationToken cancellationToken = default);
}
