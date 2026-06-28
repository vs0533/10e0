namespace TenE0.Core.Certificate;

/// <summary>
/// 证书全局样式（issue #185）。
///
/// <para>
/// 渲染器读取本对象决定字号 / 颜色 / 边距等视觉参数。所有字段带默认值，<c>TemplateJson</c>
/// 里省略 <see cref="CertificateDefinition.Styles"/> 时用全默认。业务方可定制整套视觉风格。
/// </para>
/// </summary>
public sealed class CertificateStyles
{
    /// <summary>标题字号（pt）。默认 28。</summary>
    public double TitleFontSize { get; set; } = 28;

    /// <summary>正文字号（pt）。默认 14。</summary>
    public double BodyFontSize { get; set; } = 14;

    /// <summary>姓名字号（pt，比正文略大以突出）。默认 18。</summary>
    public double NameFontSize { get; set; } = 18;

    /// <summary>页面四周页边距（pt）。默认 50。</summary>
    public double Margin { get; set; } = 50;

    /// <summary>元素之间纵向间距（pt）。默认 12。</summary>
    public double ElementSpacing { get; set; } = 12;

    /// <summary>文本颜色（CSS 颜色名或十六进制 <c>#RRGGBB</c>）。默认黑色。</summary>
    public string TextColor { get; set; } = "#000000";

    /// <summary>标题颜色。默认黑色。</summary>
    public string TitleColor { get; set; } = "#000000";

    /// <summary>分隔线颜色。默认浅灰。</summary>
    public string LineColor { get; set; } = "#CCCCCC";
}
