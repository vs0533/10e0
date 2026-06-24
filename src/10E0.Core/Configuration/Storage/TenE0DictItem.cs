using TenE0.Core.Entities;

namespace TenE0.Core.Configuration.Storage;

/// <summary>
/// 数据字典选项 — 二级结构。
///
/// <para>
/// 树形字典：通过 <see cref="ParentItemCode"/> 指向上级 item 的 <c>Code</c>（同字典类型内），
/// 根节点 <see cref="ParentItemCode"/> 为 null。注意 item 的业务唯一性是
/// <c>(DictTypeCode, Value)</c>，而非全局唯一 —— 不同字典类型可复用同一 Value。
/// </para>
/// </summary>
public class TenE0DictItem : AuditedEntity
{
    /// <summary>所属字典类型 Code（业务外键 → <see cref="TenE0DictType.Code"/>）。</summary>
    public string DictTypeCode { get; set; } = "";

    /// <summary>显示文本，如 "男"。</summary>
    public string Label { get; set; } = "";

    /// <summary>实际值，如 "M"。同 <see cref="DictTypeCode"/> 下唯一。</summary>
    public string Value { get; set; } = "";

    /// <summary>扩展字段（JSON），承载颜色/图标/分组等前端附加数据。</summary>
    public string? ExtraJson { get; set; }

    /// <summary>是否启用。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>排序权重（值越小越靠前）。</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// 父级 item 的 Value（同字典类型内），null 表示根节点。
    /// 用 Value 而非 Id 作为树形关联键，便于跨环境数据迁移。
    /// </summary>
    public string? ParentItemValue { get; set; }
}
