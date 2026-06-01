namespace TenE0.Core.Organizations;

/// <summary>
/// 组织树服务 — 提供路径维护正确的树操作。
///
/// 业务代码不要直接 dc.TenE0Orgs.Add(...) — 那样需要自己算 Path/Level，容易出错。
/// 一律走这里。
/// </summary>
public interface IOrgTreeService
{
    /// <summary>创建组织节点。parentId 为 null 时创建根。</summary>
    Task<TenE0Org> AddAsync(
        string code,
        string name,
        string? parentId = null,
        string? description = null,
        int order = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 移动子树到新父节点。会自动维护整棵子树的 Path 和 Level。
    /// newParentId 为 null = 移到根。
    /// </summary>
    Task MoveAsync(string nodeId, string? newParentId, CancellationToken cancellationToken = default);

    /// <summary>取指定节点的所有后代（不含自身）。</summary>
    Task<IReadOnlyList<TenE0Org>> GetDescendantsAsync(string nodeId, CancellationToken cancellationToken = default);

    /// <summary>取指定节点的所有祖先（根 → 父，不含自身），按层级从近到远。</summary>
    Task<IReadOnlyList<TenE0Org>> GetAncestorsAsync(string nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 取指定节点及其后代的 ID 集合（含自身）。
    /// 常用于"按部门权限隔离数据"场景：用户绑定 OrgId，可见所有子部门数据。
    /// </summary>
    Task<IReadOnlySet<string>> GetSubtreeIdsAsync(string nodeId, CancellationToken cancellationToken = default);
}
