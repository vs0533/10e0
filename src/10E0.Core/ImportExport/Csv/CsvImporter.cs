using System.Runtime.CompilerServices;
using TenE0.Core.ImportExport.Mapping;

namespace TenE0.Core.ImportExport.Csv;

/// <summary>
/// <see cref="ICsvImporter"/> 默认实现（手写 RFC 4180 状态机，不依赖 CsvHelper）。
///
/// <para>逐行流式读取（<see cref="ReadAsync{T}"/> 返回 <see cref="IAsyncEnumerable{T}"/>）。
/// 行级解析错误（类型转换失败 / 必填缺失）收集进 <see cref="ImportRow{T}.Errors"/>，不抛断流。
/// 列匹配按表头文本，不依赖列顺序。</para>
/// </summary>
public sealed class CsvImporter : ICsvImporter
{
    /// <inheritdoc/>
    public async IAsyncEnumerable<ImportRow<T>> ReadAsync<T>(
        Stream csvStream,
        ImportOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
        where T : class, new()
    {
        options ??= new ImportOptions();

        using var reader = new StreamReader(csvStream, options.Encoding, leaveOpen: true);
        var csv = new CsvReader(reader);

        var columns = MappingResolver.Resolve<T>().ImportColumns();

        // 读表头行，建立列名 → 索引
        var headerByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = csv.ReadRows().GetEnumerator();

        // 表头
        if (rows.MoveNext())
        {
            var header = rows.Current;
            for (var i = 0; i < header.Length; i++)
            {
                var name = header[i]?.Trim();
                if (!string.IsNullOrEmpty(name))
                    headerByName[name] = i;
            }
        }

        var columnIndexes = new Dictionary<ColumnMap, int>();
        foreach (var column in columns)
        {
            if (headerByName.TryGetValue(column.ColumnName, out var idx))
                columnIndexes[column] = idx;
        }

        var rowNumber = options.DataStartRow;
        while (rows.MoveNext())
        {
            ct.ThrowIfCancellationRequested();

            var fields = rows.Current;

            // 空行策略：CSV 行的"空"判定为所有字段空字符串
            if (options.IgnoreBlankRows && fields.All(f => string.IsNullOrEmpty(f)))
            {
                rowNumber++;
                continue;
            }

            yield return ParseRow<T>(rowNumber, fields, columns, columnIndexes);
            rowNumber++;
            await Task.Yield();
        }
    }

    private static ImportRow<T> ParseRow<T>(
        int rowNumber,
        string[] fields,
        IReadOnlyList<ColumnMap> columns,
        IReadOnlyDictionary<ColumnMap, int> columnIndexes)
        where T : class, new()
    {
        var errors = new List<string>();
        var entity = new T();

        foreach (var column in columns)
        {
            if (!columnIndexes.TryGetValue(column, out var idx))
            {
                if (column.Required)
                    errors.Add($"第 {rowNumber} 行：列「{column.ColumnName}」在文件中未找到（必填）");
                continue;
            }

            if (idx >= fields.Length)
            {
                if (column.Required)
                    errors.Add($"第 {rowNumber} 行：列「{column.ColumnName}」超出字段范围（必填）");
                continue;
            }

            var raw = fields[idx];

            if (string.IsNullOrWhiteSpace(raw))
            {
                if (column.Required)
                    errors.Add($"第 {rowNumber} 行：列「{column.ColumnName}」不能为空（必填）");
                continue;
            }

            if (!CsvReader.TryConvert(raw, column.Property.PropertyType, column.Format, out var converted, out var convertError))
            {
                errors.Add($"第 {rowNumber} 行：列「{column.ColumnName}」值「{raw}」无法转换为 {column.Property.PropertyType.Name}：{convertError}");
                continue;
            }

            column.Property.SetValue(entity, converted);
        }

        return new ImportRow<T>(rowNumber, entity, errors);
    }
}
