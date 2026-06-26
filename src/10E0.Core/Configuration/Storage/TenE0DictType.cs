using TenE0.Core.Entities;

namespace TenE0.Core.Configuration.Storage;

/// <summary>
/// 数据字典分类 — 一级结构。
///
/// <para>
/// 业务用 <see cref="Code"/> 作为跨环境迁移友好的业务主键（如 "gender"、"id_type"），
/// <see cref="TenE0DictItem"/> 通过 <c>DictTypeCode</c> 业务外键挂接（非数据库主外键，
/// 与既有 <c>TenE0RolePermission.RoleCode</c> 同款约定 —— 仅索引约束，无显式 FK）。
/// </para>
/// </summary>
public class TenE0DictType : AuditedEntity
{
    /// <summary>唯一编码（业务主键），如 "gender"、"id_type"。</summary>
    public string Code { get; set; } = "";

    /// <summary>显示名，如 "性别"。</summary>
    public string Name { get; set; } = "";

    /// <summary>描述。</summary>
    public string? Description { get; set; }

    /// <summary>是否启用。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>排序权重（值越小越靠前）。</summary>
    public int SortOrder { get; set; }
}
