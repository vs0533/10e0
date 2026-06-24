using System.Globalization;

namespace TenE0.Core.ImportExport.Csv;

/// <summary>
/// RFC 4180 CSV 写入器（手写，不依赖 CsvHelper）。
///
/// <para>规则：含逗号 / 双引号 / 换行 / CR 的字段用双引号包裹，字段内的双引号转义为两个双引号。
/// 行分隔符固定为 CRLF（RFC 4180 §2.2）。</para>
/// </summary>
internal sealed class CsvWriter
{
    private readonly TextWriter _writer;

    internal CsvWriter(TextWriter writer)
    {
        _writer = writer;
    }

    /// <summary>写入一行（已完成 RFC 4180 转义的字段）。</summary>
    internal void WriteRow(IEnumerable<string?> fields)
    {
        var first = true;
        foreach (var field in fields)
        {
            if (!first) _writer.Write(',');
            first = false;
            _writer.Write(Escape(field));
        }
        _writer.Write("\r\n");
    }

    /// <summary>
    /// RFC 4180 字段转义：含逗号 / 引号 / CR / LF 时用引号包裹，内部引号翻倍。
    /// </summary>
    private static string Escape(string? field)
    {
        if (field is null) return "";

        if (field.IndexOfAny([',', '"', '\r', '\n']) < 0)
            return field;

        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>
    /// 把任意值格式化为字符串。日期/数字用 InvariantCulture，套用可选 format。
    /// 与 Excel 路径的格式语义对齐。
    /// </summary>
    internal static string? FormatValue(object? value, string? format)
    {
        return value switch
        {
            null => null,
            DateTimeOffset dto => (format is not null
                ? dto.ToString(format, CultureInfo.InvariantCulture)
                : dto.ToString("O", CultureInfo.InvariantCulture)),
            DateTime dt => (format is not null
                ? dt.ToString(format, CultureInfo.InvariantCulture)
                : dt.ToString("O", CultureInfo.InvariantCulture)),
            IFormattable f => f.ToString(format, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }

    internal TextWriter Inner => _writer;
}
