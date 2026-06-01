using System.Linq.Dynamic.Core;
using System.Linq.Expressions;

namespace TenE0.Core.Queries;

/// <summary>
/// 动态 LINQ 查询扩展 — 封装 System.Linq.Dynamic.Core 的常用操作。
///
/// 用法示例：
///   var query = dbContext.Set&lt;Order&gt;().AsQueryable();
///   query = query.DynamicWhere("Status == @0 && Amount > @1", "Active", 100);
///   query = query.DynamicOrderBy("CreateTime desc, Amount asc");
///   query = query.DynamicSelect("new (Id, OrderNo, Amount)");
/// </summary>
public static class DynamicQueryExtensions
{
    /// <summary>
    /// 动态 WHERE 条件。
    /// 支持参数化：@0, @1, ... 或 @p0, @p1, ...
    /// </summary>
    public static IQueryable<T> DynamicWhere<T>(this IQueryable<T> source, string predicate, params object[] args)
    {
        if (string.IsNullOrWhiteSpace(predicate)) return source;
        return source.Where(predicate, args);
    }

    /// <summary>
    /// 动态 ORDER BY。
    /// 格式：propertyName [asc|desc], propertyName2 [asc|desc], ...
    /// 示例："CreateTime desc, Amount asc"
    /// </summary>
    public static IQueryable<T> DynamicOrderBy<T>(this IQueryable<T> source, string orderBy)
    {
        if (string.IsNullOrWhiteSpace(orderBy)) return source;
        return source.OrderBy(orderBy);
    }

    /// <summary>
    /// 动态 SELECT 投影。
    /// 格式："new (Prop1, Prop2 as Alias, Prop3)"
    /// 返回动态类型，需要后续 ToList() 或 Cast。
    /// </summary>
    public static IQueryable DynamicSelect<T>(this IQueryable<T> source, string select)
    {
        if (string.IsNullOrWhiteSpace(select)) return source;
        return source.Select(select);
    }

    /// <summary>
    /// 动态 GROUP BY + 聚合。
    /// 格式："new (Key as GroupKey, Count() as Total, Sum(Amount) as SumAmount)"
    /// </summary>
    public static IQueryable DynamicGroupBy<T>(this IQueryable<T> source, string keySelector, string resultSelector)
    {
        if (string.IsNullOrWhiteSpace(keySelector)) return source;
        return source.GroupBy($"new ({keySelector})", resultSelector);
    }

    /// <summary>
    /// 安全的分页封装。
    /// </summary>
    public static IQueryable<T> Page<T>(this IQueryable<T> source, int page, int pageSize)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 10;
        if (pageSize > 1000) pageSize = 1000;
        return source.Skip((page - 1) * pageSize).Take(pageSize);
    }

    /// <summary>
    /// 条件过滤 — 只在条件为 true 时附加 WHERE。
    /// 常用于搜索接口：query.WhereIf(!string.IsNullOrEmpty(keyword), $"Name.Contains(@0)", keyword);
    /// </summary>
    public static IQueryable<T> WhereIf<T>(this IQueryable<T> source, bool condition, string predicate, params object[] args)
    {
        return condition ? source.DynamicWhere(predicate, args) : source;
    }
}
