using System.Collections;
using System.Globalization;
using TenE0.Core.DynamicFilters;

namespace TenE0.Core.Workflow.Definitions;

/// <summary>
/// 流程分支条件求值器 — 复用 <see cref="ConditionRuleGroup"/> 模型与操作符语义，
/// 但求值对象是<b>业务数据字典</b>（<see cref="Dictionary{TKey,TValue}"/>）而非 EF 实体。
///
/// 与 <c>DynamicFilters.FilterExpressionBuilder</c> 的关系：
/// <list type="bullet">
/// <item><b>语义复用</b>：同一套操作符（eq/ne/gt/gte/lt/lte/contains/startsWith/endsWith/in/notIn）</item>
/// <item><b>实现独立</b>：FilterExpressionBuilder 输出 EF LambdaExpression（面向实体 + DbContext），
///   无法用于"运行时业务数据字典"场景。本类直接对字典求值。</item>
/// </list>
///
/// 占位符支持（与 ConditionRule.Value 约定一致）：
/// "{loginUser}" → ctx 发起人；"{loginOrg}" → ctx 发起人组织。其他值视为字面量。
/// </summary>
public static class ConditionEvaluator
{
    /// <summary>对单个 <see cref="ConditionRuleGroup"/> 求值。</summary>
    /// <param name="group">条件组。</param>
    /// <param name="data">业务数据字典。</param>
    /// <param name="initiator">发起人（用于 {loginUser} 占位符）。</param>
    /// <param name="initiatorOrgId">发起人组织（用于 {loginOrg} 占位符）。</param>
    /// <returns>组是否满足（按 Logic 聚合 Rules + Children）。</returns>
    public static bool Evaluate(
        ConditionRuleGroup group,
        IReadOnlyDictionary<string, object?> data,
        string? initiator = null,
        string? initiatorOrgId = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(data);
        return EvaluateGroup(group, data, initiator, initiatorOrgId);
    }

    private static bool EvaluateGroup(
        ConditionRuleGroup group,
        IReadOnlyDictionary<string, object?> data,
        string? initiator,
        string? initiatorOrgId)
    {
        var isAnd = !string.Equals(group.Logic, "Or", StringComparison.OrdinalIgnoreCase);

        // 子组递归
        foreach (var child in group.Children)
        {
            var childResult = EvaluateGroup(child, data, initiator, initiatorOrgId);
            if (isAnd && !childResult) return false;
            if (!isAnd && childResult) return true;
        }

        // 本层规则
        foreach (var rule in group.Rules)
        {
            var ruleResult = EvaluateRule(rule, data, initiator, initiatorOrgId);
            if (isAnd && !ruleResult) return false;
            if (!isAnd && ruleResult) return true;
        }

        // And 全部通过 → true；Or 全部失败 → false
        return isAnd;
    }

    private static bool EvaluateRule(
        ConditionRule rule,
        IReadOnlyDictionary<string, object?> data,
        string? initiator,
        string? initiatorOrgId)
    {
        data.TryGetValue(rule.Field, out var raw);
        var actual = raw;
        var expected = ResolvePlaceholder(rule.Value, initiator, initiatorOrgId);

        return rule.Op.ToLowerInvariant() switch
        {
            "eq" => ValuesEqual(actual, expected),
            "ne" => !ValuesEqual(actual, expected),
            "gt" => Compare(actual, expected) > 0,
            "gte" => Compare(actual, expected) >= 0,
            "lt" => Compare(actual, expected) < 0,
            "lte" => Compare(actual, expected) <= 0,
            "contains" => StringValue(actual)?.Contains(StringValue(expected) ?? "") ?? false,
            "startswith" => StringValue(actual)?.StartsWith(StringValue(expected) ?? "") ?? false,
            "endswith" => StringValue(actual)?.EndsWith(StringValue(expected) ?? "") ?? false,
            "in" => IsIn(actual, expected),
            "notin" => !IsIn(actual, expected),
            _ => throw new InvalidOperationException($"不支持的操作符 '{rule.Op}'"),
        };
    }

    private static string? ResolvePlaceholder(string? value, string? initiator, string? initiatorOrgId)
        => value switch
        {
            "{loginUser}" => initiator,
            "{loginOrg}" => initiatorOrgId,
            _ => value,
        };

    private static bool ValuesEqual(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        // 数值/字符串都尝试转换比较
        if (TryConvertDecimal(a, out var da) && TryConvertDecimal(b, out var db))
            return da == db;
        return string.Equals(Convert.ToString(a, CultureInfo.InvariantCulture),
            Convert.ToString(b, CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static int Compare(object? a, object? b)
    {
        if (a is null || b is null) return -1;
        if (TryConvertDecimal(a, out var da) && TryConvertDecimal(b, out var db))
            return da.CompareTo(db);
        return string.Compare(
            Convert.ToString(a, CultureInfo.InvariantCulture),
            Convert.ToString(b, CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    private static string? StringValue(object? v)
        => v?.ToString();

    private static bool IsIn(object? actual, object? expectedList)
    {
        var items = expectedList switch
        {
            IEnumerable e and not string => e.Cast<object?>(),
            string s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Cast<object?>(),
            _ => [expectedList],
        };
        foreach (var item in items)
        {
            if (ValuesEqual(actual, item)) return true;
        }
        return false;
    }

    private static bool TryConvertDecimal(object? v, out decimal result)
    {
        result = 0;
        if (v is null) return false;
        return decimal.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture),
            NumberStyles.Any, CultureInfo.InvariantCulture, out result);
    }
}
