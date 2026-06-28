using System.Globalization;

namespace TenE0.Core.Certificate;

/// <summary>
/// 数据绑定器（issue #185）—— 把 <c>IReadOnlyDictionary&lt;string, object?&gt;</c> 绑定到模板元素。
///
/// <para>
/// 纯逻辑组件（无副作用），单测友好。负责两件事：
/// <list type="bullet">
/// <item><b>取值 + 格式化</b>：按元素 <see cref="CertificateElement.Key"/> 从字典取值，按元素类型格式化
/// （<see cref="DateElement"/> 用其 <c>Format</c>，其余 <c>ToString</c>）。</item>
/// <item><b>URL scheme 安全</b>：<see cref="QrCodeElement"/> 的 URL 渲染前经 <see cref="ValidateUrlScheme"/>
/// 校验，仅放行 http/https，拒绝 <c>javascript:</c> / <c>file:</c> 等危险 scheme
/// （issue 安全考量 #3，防扫码终端触发非预期行为）。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>不执行任意代码</b>：字典值仅作字符串 / 数字 / 日期 / 图片占位符替换，<b>不</b>解析为表达式
/// （issue 安全考量 #1）。模板 DSL 是结构化对象，<b>不</b>接受用户提供的可执行模板字符串（安全考量 #2）。
/// </para>
/// </summary>
public static class DataBinder
{
    /// <summary>
    /// 按元素 Key 取值并格式化为字符串。命中字典用字典值，未命中用元素默认占位（Placeholder / Text / Label）。
    /// </summary>
    public static string Resolve(CertificateElement element, IReadOnlyDictionary<string, object?> data)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(data);

        return element switch
        {
            // Title：字典命中用字典值，否则用元素 Text
            TitleElement t when data.TryGetValue(t.Key, out var v) && v is not null => FormatValue(v, null),
            TitleElement t => t.Text,

            // Text：标签 + 字典值（命中）或标签 + Placeholder
            TextElement tx when data.TryGetValue(tx.Key, out var v) && v is not null
                => $"{tx.Label}：{FormatValue(v, null)}",
            TextElement tx => tx.Placeholder is null ? tx.Label : $"{tx.Label}：{tx.Placeholder}",

            // Name：标签 + 字典值（命中）或仅标签
            NameElement n when data.TryGetValue(n.Key, out var v) && v is not null
                => $"{n.Label}：{FormatValue(v, null)}",
            NameElement n => n.Label,

            // Date：字典值按 Format 格式化（默认 yyyy-MM-dd）
            DateElement d when data.TryGetValue(d.Key, out var v) && v is not null
                => $"{d.Label}：{FormatValue(v, d.Format)}",
            DateElement d => d.Label,

            // QrCode：URL，需过 scheme 白名单（见 ValidateUrlScheme）
            QrCodeElement q => ResolveQrUrl(q, data),

            // Image：返回 base64 或 URL 字符串原值（渲染器再解码）
            ImageElement img when data.TryGetValue(img.Key, out var v) && v is not null => FormatValue(v, null),
            ImageElement _ => string.Empty,

            // Seal / Signature / Line：静态文本（无数据绑定），返回 Label
            SealElement s => s.Label,
            SignatureElement sg => sg.Label,
            LineElement _ => string.Empty,

            _ => string.Empty,
        };
    }

    /// <summary>
    /// 校验 URL scheme 仅 http/https。拒绝 javascript: / file: / data: 等危险 scheme。
    /// 用于 <see cref="QrCodeElement"/> 渲染前。null / 空 URL 直接放行（渲染器决定是否跳过）。
    /// </summary>
    /// <exception cref="ArgumentException">URL scheme 非 http/https。</exception>
    public static string ValidateUrlScheme(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url ?? string.Empty;

        // Uri.TryCreate 能解析绝大多数合法 URL；再校验 Scheme。
        // 用 UriKind.Absolute 强制绝对 URL（二维码里的相对路径无意义）。
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException(
                $"二维码 URL 非法（无法解析为绝对 URL）：{url}。证书二维码仅接受 http/https 绝对 URL。", nameof(url));
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"二维码 URL scheme 非法：'{uri.Scheme}'。证书二维码仅接受 http/https，" +
                $"拒绝 javascript / file / data 等危险 scheme（防扫码终端触发非预期行为）。", nameof(url));
        }

        return url;
    }

    /// <summary>
    /// 解析 QrCodeElement 的最终 URL：字典命中用字典值（覆盖占位符），否则用元素 UrlPlaceholder。
    /// 支持 <c>{certificateNo}</c> 占位符（调用方渲染期替换；本方法仅校验 scheme）。
    /// </summary>
    private static string ResolveQrUrl(QrCodeElement q, IReadOnlyDictionary<string, object?> data)
    {
        string url;
        if (data.TryGetValue(q.Key, out var v) && v is not null)
            url = FormatValue(v, null);
        else if (q.UrlPlaceholder is not null)
            url = q.UrlPlaceholder;
        else
            return string.Empty;

        // 渲染期 certificateNo 可能尚未生成（占位符 {certificateNo} 未替换）——
        // 此时 scheme 已可从模板判定（https://...{certificateNo} 仍是 https）。
        // 占位符替换发生在 CertificateService 拿到编号之后；此处只做 scheme 校验。
        // 仅当 URL 完全不含占位符时才严格校验；含占位符时由 CertificateService 替换后再校验。
        if (!url.Contains('{'))
            ValidateUrlScheme(url);
        return url;
    }

    /// <summary>
    /// 把任意值格式化为字符串。日期 / DateTimeOffset 按 <paramref name="format"/> 格式化。
    /// </summary>
    private static string FormatValue(object value, string? format) => value switch
    {
        null => string.Empty,
        DateTimeOffset dto => dto.ToString(format ?? "yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString(format ?? "yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString(format ?? "yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(format, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
