using System.Collections.Concurrent;
using System.Reflection;

namespace TenE0.Core.ImportExport.Mapping;

/// <summary>
/// 列映射解析器 —— 把 attribute 声明 + fluent <see cref="IImportMapping"/> 合并为统一的
/// <see cref="ColumnMap"/> 列表，并对反射结果做进程级缓存。
///
/// <para><b>解析优先级</b>：fluent mapping 中显式声明该属性的，以其为准；其余属性回退到 attribute；
/// 无任何标记的属性按列名=属性名、顺序=声明顺序处理，默认既可导入又可导出。</para>
///
/// <para>缓存策略对齐 <c>EntityService.SequenceFieldCache</c>：类型为 key，反射只做一次。
/// Fluent mapping 本身可携带运行时声明（不缓存，按调用传入解析）。</para>
/// </summary>
public static class MappingResolver
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<ColumnMap>> AttributeCache = new();

    /// <summary>
    /// 解析 <typeparamref name="T"/> 的列映射。
    /// </summary>
    /// <param name="fluent">可选的 fluent mapping；声明过的属性以 fluent 为准，其余回退 attribute。</param>
    public static IReadOnlyList<ColumnMap> Resolve<T>(IImportMapping? fluent = null)
        => Resolve(typeof(T), fluent);

    /// <summary>非泛型入口。</summary>
    public static IReadOnlyList<ColumnMap> Resolve(Type entityType, IImportMapping? fluent = null)
    {
        var attributeColumns = AttributeCache.GetOrAdd(entityType, static t => BuildFromAttributes(t));

        if (fluent is null)
            return attributeColumns;

        // fluent 声明的属性覆盖 attribute；未在 fluent 中出现的属性保留 attribute 结果
        var fluentProps = new HashSet<PropertyInfo>(fluent.Columns.Select(c => c.Property));
        var merged = new List<ColumnMap>();

        // fluent 列在前（保持 fluent 声明顺序）
        merged.AddRange(fluent.Columns);

        // attribute 列中未被 fluent 覆盖的追加在后
        foreach (var attr in attributeColumns)
            if (!fluentProps.Contains(attr.Property))
                merged.Add(attr);

        return merged;
    }

    /// <summary>仅参与导入的列（按声明顺序）。</summary>
    public static IReadOnlyList<ColumnMap> ImportColumns(this IReadOnlyList<ColumnMap> columns)
        => columns.Where(c => c.Importable).ToList();

    /// <summary>仅参与导出的列（按 <see cref="ColumnMap.ExportOrder"/> 升序，Order 相同按声明顺序）。</summary>
    public static IReadOnlyList<ColumnMap> ExportColumns(this IReadOnlyList<ColumnMap> columns)
        => columns
            .Where(c => c.Exportable)
            .Select((c, i) => (c, i))
            .OrderBy(t => t.c.ExportOrder)
            .ThenBy(t => t.i)
            .Select(t => t.c)
            .ToList();

    private static List<ColumnMap> BuildFromAttributes(Type entityType)
    {
        var columns = new List<ColumnMap>();

        foreach (var prop in entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var importAttr = prop.GetCustomAttribute<ImportColumnAttribute>();
            var exportAttr = prop.GetCustomAttribute<ExportColumnAttribute>();
            var importIgnore = prop.GetCustomAttribute<ImportIgnoreAttribute>() is not null;
            var exportIgnore = prop.GetCustomAttribute<ExportIgnoreAttribute>() is not null;

            // 无任何导入/导出标记 且 未显式忽略 → 跳过（不无差别暴露所有属性）
            // 仅当至少有一个标记或显式忽略时才生成映射条目；这样默认只导出/导入声明的字段，
            // 避免 Password 等未声明字段被意外泄露。
            if (importAttr is null && exportAttr is null && !importIgnore && !exportIgnore)
                continue;

            // 若两边都显式忽略，则该属性不进任何映射
            if (importIgnore && exportIgnore)
                continue;

            var columnName = exportAttr?.ColumnName ?? importAttr?.ColumnName ?? prop.Name;
            var required = importAttr?.Required ?? exportAttr?.Required ?? false;

            columns.Add(new ColumnMap
            {
                Property = prop,
                ColumnName = columnName,
                ExportOrder = exportAttr?.Order ?? int.MaxValue,
                Format = exportAttr?.Format,
                // 可导入 = 有 [ImportColumn] 且未 [ImportIgnore]
                Importable = importAttr is not null && !importIgnore,
                // 可导出 = 有 [ExportColumn] 且未 [ExportIgnore]
                Exportable = exportAttr is not null && !exportIgnore,
                Required = required,
            });
        }

        return columns;
    }
}
