using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using TenE0.Core.DataContext;

namespace TenE0.Core.DynamicFilters;

// ============================================================
// 表达式树构建器 — 依赖 ConditionRule.cs 中定义的规则模型
// ============================================================

/// <summary>
/// 将 JSON 条件规则转换为 EF Core 可翻译的 LINQ 表达式树。
///
/// 核心用法：在 IEntityFilterContributor.BuildFilter 中调用 Build，
/// 将数据库中存储的 JSON 规则动态编译为 Named Query Filter。
///
/// 表达式通过 Expression.Constant(context) 引用 BaseDataContext 实例属性，
/// EF Core 在每次查询时读取这些属性值并参数化为 SQL 参数，
/// 因此无需在属性变化时重建模型。
/// </summary>
public static class FilterExpressionBuilder
{
    // 缓存反射结果，避免每次 Build 重复查找
    private static readonly MethodInfo s_stringContains =
        typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
    private static readonly MethodInfo s_stringStartsWith =
        typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;
    private static readonly MethodInfo s_stringEndsWith =
        typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string)])!;

    // 反序列化选项：大小写不敏感，兼容前端传入 camelCase 或 PascalCase
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 将 JSON 规则字符串构建为 EF Core 查询过滤表达式。
    /// </summary>
    /// <param name="ruleJson">JSON 格式的 ConditionRuleGroup。</param>
    /// <param name="entityType">被过滤的实体 CLR 类型。</param>
    /// <param name="contextType">DbContext 类型（保留参数，当前未使用）。</param>
    /// <param name="context">
    /// BaseDataContext 实例。表达式通过闭包引用此实例的属性（CurrentUserCode 等），
    /// EF Core 在每次查询时读取最新值作为 SQL 参数。
    /// </param>
    /// <returns>
    /// LambdaExpression: (TEntity entity) => bool。
    /// 最外层自动添加 BypassFilters 短路（超管绕过所有行过滤）。
    /// 规则为空时返回 null。
    /// </returns>
    public static LambdaExpression? Build(string ruleJson, Type entityType, Type contextType, BaseDataContext context)
    {
        var group = JsonSerializer.Deserialize<ConditionRuleGroup>(ruleJson, s_jsonOptions);
        if (group is null) return null;

        var entityParam = Expression.Parameter(entityType, "e");
        var contextExpr = Expression.Constant(context);

        var groupBody = BuildGroup(group, entityParam, contextExpr);
        if (groupBody is null) return null;

        // 最外层添加 BypassFilters 短路：超管 / 审计员等角色看到所有行
        // SQL 翻译为: WHERE (@bypass = 1) OR (...原始条件...)
        var bypassExpr = Expression.Property(contextExpr, nameof(BaseDataContext.BypassFilters));
        var finalBody = Expression.OrElse(bypassExpr, groupBody);

        return Expression.Lambda(finalBody, entityParam);
    }

    // ----------------------------------------------------------------
    // 递归构建条件组
    // ----------------------------------------------------------------

    private static Expression? BuildGroup(
        ConditionRuleGroup group,
        ParameterExpression entityParam,
        Expression contextExpr)
    {
        var expressions = new List<Expression>();

        // 当前层级的规则
        foreach (var rule in group.Rules)
        {
            var expr = BuildCondition(rule, entityParam, contextExpr);
            if (expr is not null)
                expressions.Add(expr);
        }

        // 子条件组（递归）
        foreach (var child in group.Children)
        {
            var childExpr = BuildGroup(child, entityParam, contextExpr);
            if (childExpr is not null)
                expressions.Add(childExpr);
        }

        if (expressions.Count == 0) return null;

        return Combine(expressions, group.Logic);
    }

    // ----------------------------------------------------------------
    // 构建单条条件
    // ----------------------------------------------------------------

    private static Expression BuildCondition(
        ConditionRule rule,
        ParameterExpression entityParam,
        Expression contextExpr)
    {
        // a. 获取实体属性
        var propInfo = entityParam.Type.GetProperty(rule.Field,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (propInfo is null)
            throw new InvalidOperationException(
                $"实体 {entityParam.Type.Name} 不存在属性 '{rule.Field}'，请检查过滤规则配置。");

        var propExpr = Expression.Property(entityParam, propInfo);
        var propType = propInfo.PropertyType;

        // b. 获取比较值表达式
        var valueExpr = BuildValueExpr(rule, propType, contextExpr, propExpr);

        // c. 根据操作符构建比较表达式
        return rule.Op.ToLowerInvariant() switch
        {
            "eq" => Expression.Equal(propExpr, valueExpr),
            "ne" => Expression.NotEqual(propExpr, valueExpr),
            "gt" => Expression.GreaterThan(propExpr, valueExpr),
            "gte" => Expression.GreaterThanOrEqual(propExpr, valueExpr),
            "lt" => Expression.LessThan(propExpr, valueExpr),
            "lte" => Expression.LessThanOrEqual(propExpr, valueExpr),

            "startswith" => Expression.Call(propExpr, s_stringStartsWith, valueExpr),
            "endswith" => Expression.Call(propExpr, s_stringEndsWith, valueExpr),
            "contains" => Expression.Call(propExpr, s_stringContains, valueExpr),

            "in" => BuildContains(propExpr, valueExpr),
            "notin" => Expression.Not(BuildContains(propExpr, valueExpr)),

            _ => throw new NotSupportedException(
                $"不支持的操作符 '{rule.Op}'。支持：eq/ne/gt/gte/lt/lte/contains/startsWith/endsWith/in/notIn")
        };
    }

    // ----------------------------------------------------------------
    // 构建比较值表达式
    // ----------------------------------------------------------------

    /// <summary>
    /// 根据 rule.Value 生成对应的表达式节点。
    /// - 特殊占位符 → 引用 BaseDataContext 实例属性
    /// - 普通字符串 → 转换为目标属性类型的常量
    /// - "in"/"notIn" → 逗号分隔解析为 string[]
    /// </summary>
    private static Expression BuildValueExpr(
        ConditionRule rule,
        Type propType,
        Expression contextExpr,
        Expression propExpr)
    {
        // ---- 特殊占位符 ----
        if (rule.Value == "{loginUser}")
        {
            // CurrentUserCode 是 string?，用 ?? "" 兜底避免 null 比较问题
            var userCodeExpr = Expression.Property(contextExpr, nameof(BaseDataContext.CurrentUserCode));
            return Expression.Coalesce(userCodeExpr, Expression.Constant(string.Empty));
        }

        if (rule.Value == "{loginRole}")
            return Expression.Property(contextExpr, nameof(BaseDataContext.CurrentRoleIds));

        if (rule.Value == "{loginOrg}")
            return Expression.Property(contextExpr, "CurrentOrgIds");

        // ---- "in" / "notIn"：逗号分隔 → string[] ----
        if (rule.Op.Equals("in", StringComparison.OrdinalIgnoreCase) ||
            rule.Op.Equals("notIn", StringComparison.OrdinalIgnoreCase))
        {
            var values = rule.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return Expression.Constant(values);
        }

        // ---- 普通值：转换为目标属性类型 ----
        var targetType = Nullable.GetUnderlyingType(propType) ?? propType;
        var converted = ConvertValue(rule.Value, targetType);

        Expression constExpr = Expression.Constant(converted, targetType);

        // Nullable<T> 属性需要提升类型以匹配
        if (Nullable.GetUnderlyingType(propType) is not null)
            constExpr = Expression.Convert(constExpr, propType);

        return constExpr;
    }

    // ----------------------------------------------------------------
    // Contains 辅助（"in" / "notIn" 共用）
    // ----------------------------------------------------------------

    /// <summary>
    /// 构建 Enumerable.Contains&lt;T&gt;(array, item) 调用。
    /// 当实体属性类型与数组元素类型不一致时（如 Guid vs string），自动转换属性为 string。
    /// </summary>
    private static Expression BuildContains(Expression propExpr, Expression arrayExpr)
    {
        var elementType = propExpr.Type;

        // 类型不一致时将属性转为 string（如 Guid 属性 vs string[] 角色 ID）
        Expression itemExpr = propExpr;
        if (elementType != typeof(string))
        {
            itemExpr = Expression.Call(propExpr, typeof(object).GetMethod(nameof(ToString))!);
            elementType = typeof(string);
        }

        var containsMethod = typeof(Enumerable)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
            .MakeGenericMethod(elementType);

        return Expression.Call(containsMethod, arrayExpr, itemExpr);
    }

    // ----------------------------------------------------------------
    // 组合表达式（And / Or）
    // ----------------------------------------------------------------

    /// <summary>
    /// 将多个布尔表达式用 And/Or 逻辑组合。
    /// 使用短路求值（AndAlso/OrElse），EF 翻译为 AND/OR。
    /// </summary>
    private static Expression Combine(List<Expression> expressions, string logic)
    {
        if (expressions.Count == 1) return expressions[0];

        var isAnd = logic.Equals("And", StringComparison.OrdinalIgnoreCase);
        return expressions.Aggregate((left, right) =>
            isAnd ? Expression.AndAlso(left, right) : Expression.OrElse(left, right));
    }

    // ----------------------------------------------------------------
    // 类型转换
    // ----------------------------------------------------------------

    /// <summary>
    /// 将字符串值转换为指定 CLR 类型。
    /// 支持：string / int / long / Guid / DateTime / DateTimeOffset / bool / decimal / float / double / enum。
    /// </summary>
    private static object ConvertValue(string value, Type targetType)
    {
        if (targetType == typeof(string)) return value;
        if (targetType == typeof(int)) return int.Parse(value);
        if (targetType == typeof(long)) return long.Parse(value);
        if (targetType == typeof(Guid)) return Guid.Parse(value);
        if (targetType == typeof(DateTime)) return DateTime.Parse(value);
        if (targetType == typeof(DateTimeOffset)) return DateTimeOffset.Parse(value);
        if (targetType == typeof(bool)) return bool.Parse(value);
        if (targetType == typeof(decimal)) return decimal.Parse(value);
        if (targetType == typeof(float)) return float.Parse(value);
        if (targetType == typeof(double)) return double.Parse(value);
        if (targetType.IsEnum) return Enum.Parse(targetType, value, ignoreCase: true);

        throw new NotSupportedException(
            $"过滤规则不支持的目标类型 '{targetType.Name}'，请扩展 ConvertValue 方法。");
    }
}
