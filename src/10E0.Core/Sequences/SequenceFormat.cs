using System.Text.RegularExpressions;

namespace TenE0.Core.Sequences;

/// <summary>
/// 流水号格式串解析器。
///
/// 把 "ORD-{yyyyMMdd}-{0000}" 这种格式串拆成几段，分别识别：
/// - 字面量段（"ORD-" / "-"）
/// - 日期段（{yyyyMMdd}）—— 推导出 bucket
/// - 序号段（{0000}）—— 决定序号宽度
///
/// 设计取舍：日期段最多一个（否则 bucket 歧义）；序号段最多一个（流水号只有一个序号）。
/// </summary>
internal static partial class SequenceFormat
{
    // 匹配 { ... }，里面要么全数字 0（序号）要么不含数字 0（日期格式串）
    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex TokenRegex();

    public record Parsed(
        IReadOnlyList<Segment> Segments,
        string? DateToken,
        int SequenceWidth);

    public abstract record Segment
    {
        public sealed record Literal(string Text) : Segment;
        public sealed record DatePlaceholder(string Format) : Segment;
        public sealed record SequencePlaceholder(int Width) : Segment;
    }

    public static Parsed Parse(string format)
    {
        if (string.IsNullOrEmpty(format))
            throw new ArgumentException("格式串不能为空", nameof(format));

        var segments = new List<Segment>();
        string? dateToken = null;
        int sequenceWidth = 0;

        var lastIndex = 0;
        foreach (Match match in TokenRegex().Matches(format))
        {
            if (match.Index > lastIndex)
                segments.Add(new Segment.Literal(format[lastIndex..match.Index]));

            var inner = match.Groups[1].Value;

            // 全是 '0' → 序号占位（{0000} = 4 位补零）
            if (inner.All(c => c == '0'))
            {
                if (sequenceWidth > 0)
                    throw new ArgumentException($"格式串只能含一个序号占位：{format}");
                sequenceWidth = inner.Length;
                segments.Add(new Segment.SequencePlaceholder(inner.Length));
            }
            else
            {
                if (dateToken is not null)
                    throw new ArgumentException($"格式串只能含一个日期占位：{format}");
                dateToken = inner;
                segments.Add(new Segment.DatePlaceholder(inner));
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < format.Length)
            segments.Add(new Segment.Literal(format[lastIndex..]));

        if (sequenceWidth == 0)
            throw new ArgumentException($"格式串必须含一个序号占位（如 {{0000}}）：{format}");

        return new Parsed(segments, dateToken, sequenceWidth);
    }

    /// <summary>根据当前时间和解析结果，渲染 bucket 字符串（用于判定是否需要归零）。</summary>
    public static string RenderBucket(Parsed parsed, DateTimeOffset now)
        => parsed.DateToken is null ? "_" : now.ToString(parsed.DateToken);

    /// <summary>把序号和当前时间填回格式串，渲染最终流水号。</summary>
    public static string Render(Parsed parsed, long number, DateTimeOffset now)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var seg in parsed.Segments)
        {
            switch (seg)
            {
                case Segment.Literal lit:
                    sb.Append(lit.Text);
                    break;
                case Segment.DatePlaceholder date:
                    sb.Append(now.ToString(date.Format));
                    break;
                case Segment.SequencePlaceholder num:
                    sb.Append(number.ToString().PadLeft(num.Width, '0'));
                    break;
            }
        }
        return sb.ToString();
    }
}
