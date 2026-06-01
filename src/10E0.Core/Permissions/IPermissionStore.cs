namespace TenE0.Core.Permissions;

/// <summary>
/// 角色到权限 key 的映射存储。
///
/// 抽象的目的：底层可以是 DB 表（生产）、内存字典（测试/demo）、外部 RBAC 系统（企业集成）。
/// 旧 E0 直接 _e0.DC.Set&lt;E0Privilege&gt;().Where(...).ToList() 硬编码 EF + 表结构，新版解耦。
/// </summary>
public interface IPermissionStore
{
    /// <summary>
    /// 取得指定角色集合并集授予的所有权限 key。
    /// 实现层可以做合理缓存。
    /// </summary>
    Task<IReadOnlySet<string>> GetGrantedPermissionsAsync(
        IReadOnlyCollection<string> roleIds,
        CancellationToken cancellationToken = default);
}
