using System.Reflection;

namespace TenE0.Core.ImportExport.Mapping;

/// <summary>
/// 标记属性为导入列 —— 列名来自源文件表头（如 "姓名"），运行时按列名匹配源列。
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ImportColumnAttribute(string columnName) : Attribute
{
    /// <summary>源文件表头列名。</summary>
    public string ColumnName { get; } = columnName;

    /// <summary>该列是否必填。默认 false。</summary>
    public bool Required { get; set; }
}

/// <summary>
/// 标记属性在导入时忽略（即便源文件有同名列也不读取）。
/// 典型：客户端不应回填的审计字段、敏感字段。
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ImportIgnoreAttribute : Attribute
{
}

/// <summary>
/// 标记属性为导出列。
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ExportColumnAttribute(string columnName) : Attribute
{
    /// <summary>目标列名（表头显示名）。</summary>
    public string ColumnName { get; } = columnName;

    /// <summary>列顺序（升序）。未设置者按声明顺序排在有 Order 者之后。默认 int.MaxValue。</summary>
    public int Order { get; set; } = int.MaxValue;

    /// <summary>值格式串（如日期 "yyyy-MM-dd"、数字 "N2"）。空表示用类型默认。</summary>
    public string? Format { get; set; }

    /// <summary>该列是否必填（仅模板校验用，导出无影响）。默认 false。</summary>
    public bool Required { get; set; }
}

/// <summary>
/// 标记属性在导出时忽略。
/// 典型：Password / Token / 身份证号等敏感字段 —— 与 <see cref="IExportFieldFilter"/> 二选一或叠加。
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ExportIgnoreAttribute : Attribute
{
}

/// <summary>
/// 单列映射的解析后描述（attribute / fluent 合并产物）。
/// </summary>
public sealed class ColumnMap
{
    /// <summary>属性反射信息。</summary>
    public required PropertyInfo Property { get; init; }

    /// <summary>源/目标列名（表头名）。</summary>
    public required string ColumnName { get; init; }

    /// <summary>导出顺序（升序）。仅导出路径排序使用。</summary>
    public int ExportOrder { get; init; } = int.MaxValue;

    /// <summary>值格式串。</summary>
    public string? Format { get; init; }

    /// <summary>是否参与导入。</summary>
    public bool Importable { get; init; } = true;

    /// <summary>是否参与导出。</summary>
    public bool Exportable { get; init; } = true;

    /// <summary>是否必填（导入校验 / 模板校验）。</summary>
    public bool Required { get; init; }
}
