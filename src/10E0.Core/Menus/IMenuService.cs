using TenE0.Core.Menus.Storage;

namespace TenE0.Core.Menus;

/// <summary>
/// 菜单服务 — 菜单的 CRUD、树查询、角色分配。
///
/// 业务代码不要直接操作 dc.TenE0Menus — 树路径（TreePath / Level）由服务统一维护，
/// 避免手动计算导致不一致。
/// </summary>
public interface IMenuService
{
    /// <summary>创建菜单节点。</summary>
    Task<TenE0Menu> AddAsync(MenuCreateRequest request, CancellationToken cancellationToken = default);

    /// <summary>更新菜单节点（仅更新 request 中非 null 字段）。</summary>
    Task UpdateAsync(string menuId, MenuUpdateRequest request, CancellationToken cancellationToken = default);

    /// <summary>删除菜单节点及其子树。</summary>
    Task DeleteAsync(string menuId, CancellationToken cancellationToken = default);

    /// <summary>移动菜单节点到新父节点。newParentId 为 null = 移到根。</summary>
    Task MoveAsync(string menuId, string? newParentId, CancellationToken cancellationToken = default);

    /// <summary>获取全部菜单（平铺列表）。</summary>
    Task<IReadOnlyList<TenE0Menu>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>获取当前用户可见菜单（平铺列表，已过滤权限）。</summary>
    Task<IReadOnlyList<TenE0Menu>> GetUserMenusAsync(CancellationToken cancellationToken = default);

    /// <summary>获取全部菜单树。</summary>
    Task<IReadOnlyList<MenuTreeNode>> GetMenuTreeAsync(CancellationToken cancellationToken = default);

    /// <summary>获取当前用户可见菜单树（已过滤权限）。</summary>
    Task<IReadOnlyList<MenuTreeNode>> GetUserMenuTreeAsync(CancellationToken cancellationToken = default);

    /// <summary>批量替换某角色的全部菜单分配。</summary>
    Task AssignToRoleAsync(string roleCode, IEnumerable<string> menuIds, CancellationToken cancellationToken = default);

    /// <summary>获取某角色已分配的菜单 ID 集合。</summary>
    Task<IReadOnlySet<string>> GetRoleMenuIdsAsync(string roleCode, CancellationToken cancellationToken = default);
}
