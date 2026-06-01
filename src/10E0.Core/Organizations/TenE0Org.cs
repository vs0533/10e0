using TenE0.Core.Entities;

namespace TenE0.Core.Organizations;

/// <summary>
/// 组织/部门实体 — 树形结构，采用"物化路径"模型。
///
/// 为什么用物化路径而不是 SQL Server 的 HierarchyId：
/// - 跨数据库可移植（Postgres / MySQL / SQLite 都一致工作）
/// - 子树查询用 LIKE 索引扫描，性能可控
/// - 路径可读（调试时一眼看出层级）
///
/// 路径约定：
/// - 用 '/' 分隔，前后都有 '/'（便于 LIKE）
/// - 路径片段是各级的 Id（GUID-N 不含分隔符，安全）
/// - 根：Path = "/{rootId}/"，Level = 0
/// - 子：Path = parent.Path + "{childId}/"，Level = parent.Level + 1
///
/// 子树查询：
///   var subtreeIds = await dc.TenE0Orgs.Where(o => o.Path.StartsWith(node.Path)).Select(o => o.Id).ToListAsync();
/// </summary>
public class TenE0Org : TreeAuditedEntity
{
    /// <summary>业务编码（唯一，便于跨系统对接）。</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// 物化路径 — 含自己的 Id，"/" 分隔，首尾各一个 "/"。
    /// 由 OrgTreeService 维护，业务代码不应直接修改。
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>层级（根 = 0）。由 OrgTreeService 维护。</summary>
    public int Level { get; set; }

    /// <summary>排序权重（同级内排序）。</summary>
    public int Order { get; set; }

    public bool IsActive { get; set; } = true;
}
