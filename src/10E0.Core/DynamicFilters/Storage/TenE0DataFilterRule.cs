using TenE0.Core.Entities;

namespace TenE0.Core.DynamicFilters.Storage;

/// <summary>
/// 动态数据过滤规则 — 按实体类型定义行级过滤条件。
///
/// 运行时 JSON 规则被转换为 EF Named Query Filter，
/// 对目标实体的所有查询自动附加 WHERE 条件。
/// </summary>
public sealed class TenE0DataFilterRule : BaseEntity
{
    /// <summary>目标实体的完整类型名（如 "MyApp.Entities.Order"）。</summary>
    public required string EntityTypeName { get; set; }

    /// <summary>JSON 格式的过滤规则（ConditionRuleGroup 结构）。</summary>
    public required string RuleJson { get; set; }

    /// <summary>是否启用此过滤规则。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>规则说明（管理界面展示用）。</summary>
    public string? Description { get; set; }
}
