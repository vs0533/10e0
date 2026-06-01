using System.ComponentModel.DataAnnotations.Schema;
using TenE0.Core.Entities;

namespace TenE0.Core.Menus.Storage;

/// <summary>
/// 菜单类型。
/// </summary>
public enum MenuType
{
    /// <summary>普通菜单项（页面路由）。</summary>
    Menu = 0,

    /// <summary>目录节点（仅用于分组，本身不对应页面）。</summary>
    Directory = 1,

    /// <summary>按钮/权限点（不产生路由，仅用于权限控制）。</summary>
    Button = 2,

    /// <summary>外链（点击后跳转至外部 URL）。</summary>
    Link = 3
}

/// <summary>
/// 菜单实体 — 树形结构，采用物化路径模型（与 TenE0Org 一致）。
///
/// 路径约定：
/// - TreePath 格式："/{id1}/{id2}/{id3}/"，首尾各一个 "/"
/// - 根节点：TreePath = "/{rootId}/"，Level = 0
/// - 子节点：TreePath = parent.TreePath + "{childId}/"，Level = parent.Level + 1
///
/// 子树查询：
///   var subtree = await dc.TenE0Menus.Where(m => m.TreePath.StartsWith(node.TreePath)).ToListAsync();
/// </summary>
public class TenE0Menu : TreeAuditedEntity
{
    /// <summary>菜单名称。</summary>
    public required string Name { get; set; }

    /// <summary>前端路由路径（用 RoutePath 避免与物化路径 TreePath 冲突）。</summary>
    public required string RoutePath { get; set; }

    /// <summary>菜单图标（前端图标组件名称或 URL）。</summary>
    public string? Icon { get; set; }

    /// <summary>前端组件路径（对应 Vue/React 等框架的组件文件）。</summary>
    public string? Component { get; set; }

    /// <summary>重定向目标路径（当节点本身无页面时，自动跳转至指定子路由）。</summary>
    public string? Redirect { get; set; }

    /// <summary>是否使用布局组件包裹（默认 true，即带侧边栏/顶栏的完整布局）。</summary>
    public bool Layout { get; set; } = true;

    /// <summary>同级排序权重（值越小越靠前）。</summary>
    public int Order { get; set; }

    /// <summary>是否启用（false 时前端不渲染该菜单）。</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>菜单类型（菜单 / 目录 / 按钮 / 外链）。</summary>
    public MenuType MenuType { get; set; } = MenuType.Menu;

    /// <summary>
    /// 物化路径 — 含自己的 Id，"/" 分隔，首尾各一个 "/"。
    /// 由 MenuTreeService 维护，业务代码不应直接修改。
    /// </summary>
    public string TreePath { get; set; } = "";

    /// <summary>层级（根 = 0）。由 MenuTreeService 维护。</summary>
    public int Level { get; set; }

    /// <summary>子菜单集合（仅用于查询后在内存中构建树，不映射到数据库）。</summary>
    [NotMapped]
    public List<TenE0Menu>? Children { get; set; }
}
