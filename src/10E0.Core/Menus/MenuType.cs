namespace TenE0.Core.Menus;

/// <summary>
/// 菜单节点类型。
///
/// 三级粒度，对应前端菜单树的常见分层：
/// - <see cref="Directory"/>：目录节点，用于分组，不可直接导航
/// - <see cref="Menu"/>：菜单项，可导航到具体页面
/// - <see cref="Button"/>：按钮/操作，用于页面内细粒度权限控制
/// </summary>
public enum MenuType
{
    Directory = 0,
    Menu = 1,
    Button = 2
}
