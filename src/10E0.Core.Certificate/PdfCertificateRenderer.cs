using System.Globalization;
using Microsoft.Extensions.Options;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Snippets.Font;
using QRCoder;

namespace TenE0.Core.Certificate.Pdf;

/// <summary>
/// 基于 PDFsharp 6.2 的默认证书渲染器（issue #185）。
///
/// <para>
/// 把 <see cref="CertificateDefinition"/> + 绑定数据渲染为单页 PDF（A4/A5/Letter × 纵向/横向）。
/// 元素自上而下流式布局：标题居中大字 → 文本/姓名/日期行 → 二维码（QRCoder）→ 盖章位/签字位 → 分隔线。
/// </para>
///
/// <para>
/// <b>中文字体</b>：Core build（非 Windows / 无 GDI+）需设 <see cref="GlobalFontSettings.FontResolver"/>。
/// 本渲染器在静态构造期设 <see cref="FailsafeFontResolver"/>（PDFsharp 内置，缺字回退方块），
/// 实际字体名取 <see cref="CertificateOptions.DefaultFont"/>（默认"Microsoft YaHei"）。运维需预装中文字体。
/// </para>
///
/// <para>
/// <b>二维码</b>：<see cref="QrCodeElement"/> 用 QRCoder <see cref="PngByteQRCode"/> 生成 PNG 字节 →
/// <see cref="XImage.FromStream"/> 嵌入。URL 已由 <see cref="DataBinder.ValidateUrlScheme"/> 校验 scheme。
/// </para>
/// </summary>
public sealed class PdfCertificateRenderer(
    IOptions<CertificateOptions> options) : ICertificateRenderer
{
    /// <inheritdoc />
    public string Format => "pdf";

    static PdfCertificateRenderer()
    {
        // Core build（无 GDI+）需要 FontResolver 兜底，否则缺字时 XFont 构造可能抛异常。
        // PDFsharp 6 区分 FontResolver（主解析器，默认走系统字体）与 FallbackFontResolver（兜底）。
        // 这里设 FallbackFontResolver = FailsafeFontResolver（PDFsharp 内置，缺字回退方块而非崩溃），
        // 不覆盖主 FontResolver —— 让系统字体解析（含中文字体）保持优先。
        // 仅当 FallbackFontResolver 未被业务方设置时赋值，避免重复设置抛 ArgumentException。
        if (GlobalFontSettings.FallbackFontResolver is null)
        {
            GlobalFontSettings.UseWindowsFontsUnderWindows = true;
            GlobalFontSettings.FallbackFontResolver = new FailsafeFontResolver();
        }
    }

    /// <inheritdoc />
    public Task<Stream> RenderAsync(
        CertificateDefinition definition,
        IReadOnlyDictionary<string, object?> data,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(data);

        var fontName = options.Value.DefaultFont;
        var styles = definition.Styles ?? new CertificateStyles();

        using var document = new PdfDocument();
        var page = document.AddPage();
        ConfigurePageSize(page, definition.PaperKind, definition.Orientation);

        using var gfx = XGraphics.FromPdfPage(page);
        var width = page.Width.Point;
        var margin = styles.Margin;
        var innerWidth = width - 2 * margin;
        var y = margin;

        // 标题居中（如果有 TitleElement；否则用 definition.Title）
        var titleEl = definition.Elements.OfType<TitleElement>().FirstOrDefault();
        var titleText = titleEl is null ? definition.Title : DataBinder.Resolve(titleEl, data);
        if (!string.IsNullOrWhiteSpace(titleText))
        {
            y = DrawCentered(gfx, titleText, fontName, styles.TitleFontSize, XColorFromCss(styles.TitleColor),
                width, y, bold: true);
            y += styles.ElementSpacing * 2;
        }

        // 主体元素（跳过 TitleElement 已画）
        foreach (var element in definition.Elements.Where(e => e is not TitleElement))
        {
            y = DrawElement(gfx, element, data, fontName, styles, margin, innerWidth, ref y);
        }

        // 落到 MemoryStream（IFileService 需要 Stream；调用方负责 Dispose）
        var ms = new MemoryStream();
        document.Save(ms);
        ms.Position = 0;
        return Task.FromResult<Stream>(ms);
    }

    private static void ConfigurePageSize(PdfPage page, PaperKind kind, CertificateOrientation orientation)
    {
        // A4 = 595×842 pt, A5 = 420×595 pt, Letter = 612×792 pt（PDF point, 1pt=1/72in）。
        (double w, double h) = kind switch
        {
            PaperKind.A4 => (595, 842),
            PaperKind.A5 => (420, 595),
            PaperKind.Letter => (612, 792),
            _ => (595, 842),
        };
        if (orientation == CertificateOrientation.Landscape)
            (w, h) = (h, w);
        // 显式 Width/Height 覆盖默认 PageSize —— 不要再赋 page.Size（PDFsharp 6 在 Width/Height 已设后
        // 改 Size 会抛 "Invalid PageSize"，因为 Size 与 Width/Height 互斥管理）。
        page.Width = XUnit.FromPoint(w);
        page.Height = XUnit.FromPoint(h);
    }

    private static double DrawElement(
        XGraphics gfx, CertificateElement element, IReadOnlyDictionary<string, object?> data,
        string fontName, CertificateStyles styles, double margin, double innerWidth, ref double y)
    {
        switch (element)
        {
            case TextElement:
            case NameElement:
            case DateElement:
                {
                    var text = DataBinder.Resolve(element, data);
                    var size = element is NameElement ? styles.NameFontSize : styles.BodyFontSize;
                    y = DrawLeft(gfx, text, fontName, size, XColorFromCss(styles.TextColor), margin, y,
                        bold: element is NameElement);
                    y += styles.ElementSpacing;
                    break;
                }
            case QrCodeElement qr:
                {
                    y = DrawQrCode(gfx, qr, data, margin, y);
                    y += styles.ElementSpacing * 2;
                    break;
                }
            case ImageElement img:
                {
                    y = DrawImage(gfx, img, data, margin, y);
                    y += styles.ElementSpacing;
                    break;
                }
            case SealElement seal:
                {
                    // 盖章位：带边框方框 + 单位名（占位，实际印章线下补盖）。
                    var text = DataBinder.Resolve(seal, data);
                    var boxHeight = 80;
                    gfx.DrawRectangle(XPens.Gray, margin, y, 200, boxHeight);
                    y = DrawCenteredInRect(gfx, text, fontName, styles.BodyFontSize,
                        XColorFromCss(styles.TextColor), margin, y, 200, boxHeight);
                    y += boxHeight + styles.ElementSpacing;
                    break;
                }
            case SignatureElement sig:
                {
                    // 签字位：下划线 + 标签。
                    var text = DataBinder.Resolve(sig, data);
                    gfx.DrawLine(XPens.Black, margin, y + 20, margin + 200, y + 20);
                    y = DrawLeft(gfx, text, fontName, styles.BodyFontSize, XColorFromCss(styles.TextColor),
                        margin, y + 25, bold: false);
                    y += styles.ElementSpacing * 2;
                    break;
                }
            case LineElement line:
                {
                    var color = string.IsNullOrWhiteSpace(line.Color)
                        ? XColorFromCss(styles.LineColor) : XColorFromCss(line.Color);
                    gfx.DrawLine(new XPen(color, 1), margin, y, margin + innerWidth, y);
                    y += styles.ElementSpacing * 2;
                    break;
                }
        }
        return y;
    }

    private static double DrawCentered(XGraphics gfx, string text, string fontName, double size,
        XColor color, double pageWidth, double y, bool bold)
    {
        var font = new XFont(fontName, size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);
        gfx.DrawString(text, font, new XSolidBrush(color),
            new XRect(0, y, pageWidth, size + 4), XStringFormats.Center);
        return y + size + 4;
    }

    private static double DrawLeft(XGraphics gfx, string text, string fontName, double size,
        XColor color, double x, double y, bool bold)
    {
        var font = new XFont(fontName, size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);
        gfx.DrawString(text, font, new XSolidBrush(color),
            new XRect(x, y, size + 4, size + 4), XStringFormats.TopLeft);
        return y + size + 4;
    }

    private static double DrawCenteredInRect(XGraphics gfx, string text, string fontName, double size,
        XColor color, double x, double y, double w, double h)
    {
        var font = new XFont(fontName, size, XFontStyleEx.Regular);
        gfx.DrawString(text, font, new XSolidBrush(color),
            new XRect(x, y, w, h), XStringFormats.Center);
        return y;
    }

    private static double DrawQrCode(XGraphics gfx, QrCodeElement qr,
        IReadOnlyDictionary<string, object?> data, double x, double y)
    {
        // 取最终 URL（含 {certificateNo} 占位符替换，DataBinder 已做含占位符 URL 的跳过校验）。
        var url = ResolveQrUrl(qr, data);
        if (string.IsNullOrWhiteSpace(url)) return y;

        // 占位符替换：certificateNo 已由 CertificateService 注入 data 副本。
        var qrData = data.TryGetValue("certificateNo", out var no) && no is not null
            ? url.Replace("{certificateNo}", no.ToString(), StringComparison.Ordinal)
            : url;

        // 替换后再次校验 scheme（占位符可能影响 URL 合法性）。
        DataBinder.ValidateUrlScheme(qrData);

        // 生成二维码矩阵。不嵌入 PNG（PDFsharp Core build 不支持 PNG 解码，仅 JPEG/Skia），
        // 直接读 ModuleMatrix（List<BitArray>）+ 用 XGraphics.DrawRectangle 画黑白方块 ——
        // 零图像格式依赖，跨平台稳定，且不依赖 System.Drawing（QRCode 类才需要）。
        using var qrGenerator = new QRCodeGenerator();
        using var qrDataObj = qrGenerator.CreateQrCode(qrData, QRCodeGenerator.ECCLevel.Q);
        var matrix = qrDataObj.ModuleMatrix;
        var moduleCount = matrix.Count;
        const double qrTotalSize = 120;
        var moduleSize = qrTotalSize / moduleCount;

        for (var row = 0; row < moduleCount; row++)
        {
            var bits = matrix[row];
            for (var col = 0; col < moduleCount; col++)
            {
                if (bits[col]) // true = 暗模块
                {
                    gfx.DrawRectangle(XBrushes.Black,
                        x + col * moduleSize, y + row * moduleSize, moduleSize, moduleSize);
                }
            }
        }
        return y + qrTotalSize;
    }

    private static string ResolveQrUrl(QrCodeElement qr, IReadOnlyDictionary<string, object?> data)
    {
        if (data.TryGetValue(qr.Key, out var v) && v is not null)
            return v?.ToString() ?? string.Empty;
        return qr.UrlPlaceholder ?? string.Empty;
    }

    private static double DrawImage(XGraphics gfx, ImageElement img,
        IReadOnlyDictionary<string, object?> data, double x, double y)
    {
        if (!data.TryGetValue(img.Key, out var v) || v is null) return y;

        XImage? image = null;
        switch (v)
        {
            case byte[] bytes:
                using (var ms = new MemoryStream(bytes))
                    image = XImage.FromStream(ms);
                break;
            case string s when s.StartsWith("data:", StringComparison.OrdinalIgnoreCase):
                // data:image/png;base64,xxxx → 解码 base64
                var commaIdx = s.IndexOf(',');
                if (commaIdx > 0 && commaIdx < s.Length - 1)
                {
                    var base64 = s[(commaIdx + 1)..];
                    var bytes = Convert.FromBase64String(base64);
                    using var ms = new MemoryStream(bytes);
                    image = XImage.FromStream(ms);
                }
                break;
        }

        if (image is null) return y;
        using (image)
        {
            gfx.DrawImage(image, x, y, img.Width, img.Height);
        }
        return y + img.Height;
    }

    /// <summary>
    /// 解析 CSS 颜色字符串（十六进制 <c>#RRGGBB</c> 或已知颜色名）为 <see cref="XColor"/>。
    /// 解析失败回退黑色。
    /// </summary>
    private static XColor XColorFromCss(string? css)
    {
        if (string.IsNullOrWhiteSpace(css)) return XColors.Black;
        if (css.StartsWith('#') && css.Length == 7)
        {
            var r = byte.Parse(css.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var g = byte.Parse(css.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var b = byte.Parse(css.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return XColor.FromArgb(r, g, b);
        }
        return XColors.Black;
    }
}
