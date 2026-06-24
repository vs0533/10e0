using System.Globalization;
using System.Text;

namespace TenE0.Core.ImportExport.Csv;

/// <summary>
/// RFC 4180 CSV 读取器（手写状态机，不依赖 CsvHelper）。
///
/// <para>支持：引号包裹字段、引号内转义的双引号（<c>""</c> → <c>"</c>）、
/// 引号内的 CR/LF、CRLF 与 LF 行分隔。</para>
/// <para>逐行流式产出（<see cref="ReadRows"/> 返回 <see cref="IEnumerable{T}"/>），
/// 调用方（<see cref="CsvImporter"/>）按需转 async。</para>
/// </summary>
internal sealed class CsvReader
{
    private readonly TextReader _reader;

    internal CsvReader(TextReader reader)
    {
        _reader = reader;
    }

    /// <summary>
    /// 逐行产出字段数组。CR/LF/CRLF 均作为行分隔；引号内的换行不分行。
    /// 文件末尾若无换行，最后一行照常产出。
    /// </summary>
    internal IEnumerable<string[]> ReadRows()
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false; // 当前字段是否已开始（区分空行与一行空字段）
        int ch;

        while ((ch = _reader.Read()) >= 0)
        {
            var c = (char)ch;

            if (inQuotes)
            {
                if (c == '"')
                {
                    // 紧跟另一个 " → 转义的引号；否则引号结束
                    var next = _reader.Peek();
                    if (next == '"')
                    {
                        _reader.Read();
                        current.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    fieldStarted = true;
                    break;
                case ',':
                    fields.Add(current.ToString());
                    current.Clear();
                    fieldStarted = false;
                    break;
                case '\r':
                    // CR 或 CRLF：行结束
                    if (_reader.Peek() == '\n') _reader.Read();
                    fields.Add(current.ToString());
                    yield return fields.ToArray();
                    fields.Clear();
                    current.Clear();
                    fieldStarted = false;
                    break;
                case '\n':
                    fields.Add(current.ToString());
                    yield return fields.ToArray();
                    fields.Clear();
                    current.Clear();
                    fieldStarted = false;
                    break;
                default:
                    current.Append(c);
                    fieldStarted = true;
                    break;
            }
        }

        // 尾部：最后一个字段或最后一行（无换行结尾的情况）
        if (fieldStarted || current.Length > 0 || fields.Count > 0)
        {
            fields.Add(current.ToString());
            yield return fields.ToArray();
        }
    }

    /// <summary>
    /// 把字符串值转换为目标类型（复用 importer 的语义，与 Excel 路径一致）。
    /// </summary>
    internal static bool TryConvert(
        string raw,
        Type targetType,
        string? format,
        out object? value,
        out string error)
    {
        error = string.Empty;
        var effectiveType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        var culture = CultureInfo.InvariantCulture;

        try
        {
            if (effectiveType == typeof(string)) { value = raw; return true; }
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
            if (effectiveType == typeof(decimal)) { value = decimal.Parse(raw, culture); return true; }
            if (effectiveType == typeof(double)) { value = double.Parse(raw, culture); return true; }
            if (effectiveType == typeof(float)) { value = float.Parse(raw, culture); return true; }
            if (effectiveType == typeof(long)) { value = long.Parse(raw, culture); return true; }
            if (effectiveType == typeof(int)) { value = int.Parse(raw, culture); return true; }
            if (effectiveType == typeof(short)) { value = short.Parse(raw, culture); return true; }
            if (effectiveType == typeof(byte)) { value = byte.Parse(raw, culture); return true; }
            if (effectiveType == typeof(bool)) { value = bool.Parse(raw); return true; }
            if (effectiveType == typeof(Guid)) { value = Guid.Parse(raw); return true; }
            if (effectiveType.IsEnum) { value = Enum.Parse(effectiveType, raw, ignoreCase: true); return true; }

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
