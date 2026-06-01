namespace TenE0.Core.DynamicFilters;

/// <summary>
/// 条件规则组 — JSON 规则树的节点。
/// 对应旧版 ConditionRuleGroup，但字段命名更规范。
///
/// JSON 示例：
/// {
///   "logic": "And",
///   "rules": [
///     { "field": "CreateBy", "op": "eq", "value": "{loginUser}" },
///     { "field": "OrgId", "op": "in", "value": "{loginOrg}" }
///   ],
///   "children": [
///     {
///       "logic": "Or",
///       "rules": [
///         { "field": "Status", "op": "eq", "value": "Active" }
///       ]
///     }
///   ]
/// }
/// </summary>
public class ConditionRuleGroup
{
    /// <summary>组合逻辑: "And" 或 "Or"。</summary>
    public string Logic { get; set; } = "And";

    /// <summary>本层的条件规则列表。</summary>
    public List<ConditionRule> Rules { get; set; } = [];

    /// <summary>嵌套子组。</summary>
    public List<ConditionRuleGroup> Children { get; set; } = [];
}

/// <summary>
/// 单条条件规则。
/// </summary>
public class ConditionRule
{
    /// <summary>实体属性名（如 "CreateBy", "OrgId", "Status"）。</summary>
    public required string Field { get; set; }

    /// <summary>操作符: eq, ne, gt, gte, lt, lte, contains, startsWith, endsWith, in, notIn。</summary>
    public required string Op { get; set; }

    /// <summary>
    /// 比较值。支持占位符：
    /// - "{loginUser}"    → 当前用户 Code
    /// - "{loginRole}"    → 当前用户角色 ID 列表
    /// - "{loginOrg}"     → 当前用户所属组织 ID 列表
    /// - 其他值            → 字面量
    /// </summary>
    public required string Value { get; set; }
}

// ─── 管理 API 用的 DTO ───

/// <summary>创建数据过滤规则请求。</summary>
public record DataFilterRuleCreateRequest(
    string EntityTypeName,
    string RuleJson,
    string? Description,
    bool IsEnabled = true
);

/// <summary>更新数据过滤规则请求 — 所有字段可空，null 表示不修改。</summary>
public record DataFilterRuleUpdateRequest(
    string? RuleJson,
    string? Description,
    bool? IsEnabled
);
