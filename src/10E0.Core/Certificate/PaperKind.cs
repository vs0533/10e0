namespace TenE0.Core.Certificate;

/// <summary>
/// 证书纸张规格（issue #185）。
///
/// <para>
/// 决定渲染器（默认 <c>PdfCertificateRenderer</c>）输出文档的页面尺寸。
/// 证书最常用 A4 横向；A5 用于紧凑卡片；Letter 用于北美场景。
/// </para>
/// </summary>
public enum PaperKind
{
    /// <summary>A4（210 × 297 mm，最常用）。</summary>
    A4,

    /// <summary>A5（148 × 210 mm，紧凑卡片）。</summary>
    A5,

    /// <summary>US Letter（8.5 × 11 in，北美场景）。</summary>
    Letter,
}

/// <summary>
/// 证书页面方向。证书最常用横向（<see cref="Landscape"/>）以容纳标题 + 主体内容。
/// </summary>
public enum CertificateOrientation
{
    /// <summary>纵向（宽 &lt; 高）。</summary>
    Portrait,

    /// <summary>横向（宽 &gt; 高）。</summary>
    Landscape,
}
