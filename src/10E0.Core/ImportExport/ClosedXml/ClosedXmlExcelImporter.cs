using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using ClosedXML.Excel;
using TenE0.Core.ImportExport.Mapping;

namespace TenE0.Core.ImportExport.ClosedXml;

/// <summary>
/// <see cref="IExcelImporter"/> 默认实现（ClosedXML）。
///
/// <para>逐行流式读取（<see cref="ReadAsync{T}"/> 返回 <see cref="IAsyncEnumerable{T}"/>），
/// 行级解析错误（类型转换失败 / 必填缺失）收集进 <see cref="ImportRow{T}.Errors"/>，不抛断流。</para>
/// <para>列匹配按 <see cref="ImportColumnAttribute.ColumnName"/> / 表头文本，不依赖列顺序。</para>
/// </summary>
public sealed class ClosedXmlExcelImporter : IExcelImporter
{
    /// <inheritdoc/>
    public async IAsyncEnumerable<ImportRow<T>> ReadAsync<T>(
        Stream excelStream,
        ImportOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
        where T : class, new()
    {
        options ??= new ImportOptions();

        using var wb = new XLWorkbook(excelStream);
        var ws = wb.Worksheets.First();

        var columns = MappingResolver.Resolve<T>().ImportColumns();
        var columnIndexes = ResolveColumnIndexes(ws, columns, options.HeaderRow);

        // 从数据起始行迭代到最后一行
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        for (var rowNumber = options.DataStartRow; rowNumber <= lastRow; rowNumber++)
        {
            ct.ThrowIfCancellationRequested();

            var xlRow = ws.Row(rowNumber);
            if (options.IgnoreBlankRows && IsBlankRow(xlRow, columnIndexes))
            {
                continue;
            }

            yield return ParseRow<T>(rowNumber, xlRow, columns, columnIndexes);
            await Task.Yield();
        }
    }

    private static bool IsBlankRow(IXLRow row, IReadOnlyDictionary<PropertyInfo, int> columnIndexes)
    {
        foreach (var (_, colIdx) in columnIndexes)
        {
            if (!row.Cell(colIdx).IsEmpty()) return false;
        }
        return true;
    }

    /// <summary>
    /// 按列名匹配源列索引。未匹配到的列在导入时跳过（不报错，兼容模板多列场景）。
    /// 用 PropertyInfo 作 key（而非 ColumnMap 引用）—— fluent mapping 重建 ColumnMap 时引用相等会失效。
    /// </summary>
    private static Dictionary<PropertyInfo, int> ResolveColumnIndexes(
        IXLWorksheet ws,
        IReadOnlyList<ColumnMap> columns,
        int headerRow)
    {
        var result = new Dictionary<PropertyInfo, int>();
        if (columns.Count == 0) return result;

        // 建立表头名 → 列索引 的反查表
        var headerByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        for (var c = 1; c <= lastCol; c++)
        {
            var headerText = ws.Cell(headerRow, c).GetValue<string>()?.Trim();
            if (!string.IsNullOrEmpty(headerText))
                headerByName[headerText] = c;
        }

        foreach (var column in columns)
        {
            if (headerByName.TryGetValue(column.ColumnName, out var idx))
                result[column.Property] = idx;
            // 未找到表头：必填列后续会在 ParseRow 报"必填缺失"，非必填则静默跳过
        }

        return result;
    }

    private static ImportRow<T> ParseRow<T>(
        int rowNumber,
        IXLRow xlRow,
        IReadOnlyList<ColumnMap> columns,
        IReadOnlyDictionary<PropertyInfo, int> columnIndexes)
        where T : class, new()
    {
        var errors = new List<string>();
        var entity = new T();

        foreach (var column in columns)
        {
            // 表头未匹配：必填则报错，非必填跳过
            if (!columnIndexes.TryGetValue(column.Property, out var colIdx))
            {
                if (column.Required)
                    errors.Add($"第 {rowNumber} 行：列「{column.ColumnName}」在文件中未找到（必填）");
                continue;
            }

            var cell = xlRow.Cell(colIdx);
            var raw = cell.IsEmpty() ? null : cell.Value.ToString();

            if (string.IsNullOrWhiteSpace(raw))
            {
                if (column.Required)
                    errors.Add($"第 {rowNumber} 行：列「{column.ColumnName}」不能为空（必填）");
                continue;
            }

            if (!TryConvert(raw, column.Property.PropertyType, column.Format, out var converted, out var convertError))
            {
                errors.Add($"第 {rowNumber} 行：列「{column.ColumnName}」值「{raw}」无法转换为 {column.Property.PropertyType.Name}：{convertError}");
                continue;
            }

            column.Property.SetValue(entity, converted);
        }

        return new ImportRow<T>(rowNumber, entity, errors);
    }

    /// <summary>
    /// 字符串 → 目标类型的转换。支持常见基元类型 + DateTime/DateTimeOffset/decimal/Guid/enum。
    /// </summary>
    private static bool TryConvert(
        string raw,
        Type targetType,
        string? format,
        out object? value,
        out string error)
    {
        error = string.Empty;
        var nullableUnderlying = Nullable.GetUnderlyingType(targetType);
        var effectiveType = nullableUnderlying ?? targetType;

        try
        {
            var culture = CultureInfo.InvariantCulture;

            if (effectiveType == typeof(string))
            {
                value = raw;
                return true;
            }
            if (effectiveType == typeof(DateTimeOffset))
            {
                value = format is not null
                    ? DateTimeOffset.ParseExact(raw, format, culture)
                    : DateTimeOffset.Parse(raw, culture);
                return true;
            }
            if (effectiveType == typeof(DateTime))
            {
                value = format is not null
                    ? DateTime.ParseExact(raw, format, culture)
                    : DateTime.Parse(raw, culture);
                return true;
            }
            if (effectiveType == typeof(decimal))
            {
                value = decimal.Parse(raw, culture);
                return true;
            }
            if (effectiveType == typeof(double))
            {
                value = double.Parse(raw, culture);
                return true;
            }
            if (effectiveType == typeof(float))
            {
                value = float.Parse(raw, culture);
                return true;
            }
            if (effectiveType == typeof(long))
            {
                value = long.Parse(raw, culture);
                return true;
            }
            if (effectiveType == typeof(int))
            {
                value = int.Parse(raw, culture);
                return true;
            }
            if (effectiveType == typeof(short))
            {
                value = short.Parse(raw, culture);
                return true;
            }
            if (effectiveType == typeof(byte))
            {
                value = byte.Parse(raw, culture);
                return true;
            }
            if (effectiveType == typeof(bool))
            {
                value = bool.Parse(raw);
                return true;
            }
            if (effectiveType == typeof(Guid))
            {
                value = Guid.Parse(raw);
                return true;
            }
            if (effectiveType.IsEnum)
            {
                value = Enum.Parse(effectiveType, raw, ignoreCase: true);
                return true;
            }

            value = Convert.ChangeType(raw, effectiveType, culture);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            value = null;
            return false;
        }
    }
}
