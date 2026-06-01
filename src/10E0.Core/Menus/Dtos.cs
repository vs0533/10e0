namespace TenE0.Core.Menus;

/// <summary>创建菜单请求。</summary>
public record MenuCreateRequest(
    string Name,
    string RoutePath,
    string? ParentId,
    string? Icon,
    string? Component,
    string? Redirect,
    bool Layout,
    int Order,
    MenuType MenuType);

/// <summary>
/// 更新菜单请求 — 所有字段可空，null 表示不修改。
/// </summary>
public record MenuUpdateRequest(
    string? Name,
    string? RoutePath,
    string? Icon,
    string? Component,
    string? Redirect,
    bool? Layout,
    int? Order,
    MenuType? MenuType,
    bool? IsActive);

/// <summary>
/// 菜单树节点 — class（需要可变 Children 集合用于构建树）。
/// </summary>
public class MenuTreeNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string RoutePath { get; set; } = "";
    public string? Icon { get; set; }
    public string? Component { get; set; }
    public string? Redirect { get; set; }
    public bool Layout { get; set; }
    public int Order { get; set; }
    public MenuType MenuType { get; set; }
    public bool IsActive { get; set; }
    public List<MenuTreeNode> Children { get; set; } = new();
}
