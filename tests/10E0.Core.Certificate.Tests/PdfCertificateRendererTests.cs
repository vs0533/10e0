// 命名空间 TenE0.Core.Certificate.Tests 嵌套在 TenE0.Core.Certificate 下，父命名空间隐式可见，无需显式 using。
using PdfCertificateRendererLocal = TenE0.Core.Certificate.Pdf.PdfCertificateRenderer;

namespace TenE0.Core.Certificate.Tests;

/// <summary>
/// <see cref="PdfCertificateRenderer"/> 单元测试（issue #185 独立包）。
///
/// <para>
/// 验证渲染器对各元素类型的处理 + 输出是合法 PDF 流（<c>%PDF-</c> 魔数）+ URL scheme 安全校验集成。
/// 不做像素级断言（跨平台字体/渲染差异），仅断言"渲染成功 + 输出可识别为 PDF"。
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class PdfCertificateRendererTests
{
    private static PdfCertificateRendererLocal CreateRenderer(string? sequenceKey = null) =>
        new(Microsoft.Extensions.Options.Options.Create(new CertificateOptions
        {
            DefaultFont = "Microsoft YaHei",
            SequenceKey = sequenceKey,
        }));

    private static async Task<Stream> RenderAsync(CertificateDefinition def,
        IReadOnlyDictionary<string, object?>? data = null)
    {
        var renderer = CreateRenderer();
        return await renderer.RenderAsync(def, data ?? new Dictionary<string, object?>());
    }

    private static void ShouldBeValidPdf(Stream stream)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var buffer = new char[5];
        reader.Read(buffer, 0, 5);
        // PDF 魔数："%PDF-"（ISO 32000）。
        new string(buffer).Should().Be("%PDF-", "渲染产物必须是合法 PDF 流");
    }

    [Fact]
    public async Task RenderAsync_TitleOnly_ProducesValidPdf()
    {
        var def = new CertificateDefinition("测试证书", PaperKind.A4, CertificateOrientation.Portrait,
            [new TitleElement("title", "结业证书")]);

        using var stream = await RenderAsync(def);

        ShouldBeValidPdf(stream);
    }

    [Fact]
    public void Format_IsPdf()
    {
        var renderer = CreateRenderer();
        renderer.Format.Should().Be("pdf");
    }

    [Fact]
    public async Task RenderAsync_AllElementTypes_RendersWithoutThrowing()
    {
        // 覆盖全部 9 种元素类型 + 数据绑定（除 LineElement 无绑定外都给 data）。
        var def = new CertificateDefinition("全元素证书", PaperKind.A4, CertificateOrientation.Landscape,
        [
            new TitleElement("title", "结业证书"),
            new TextElement("projectNo", "项目编号"),
            new NameElement("leader"),
            new DateElement("issueDate"),
            new QrCodeElement("qr", "https://verify.example.com/{certificateNo}"),
            new SealElement("seal", "签发单位"),
            new SignatureElement("sig"),
            new LineElement("line"),
        ]);
        var data = new Dictionary<string, object?>
        {
            ["projectNo"] = "PRD-001",
            ["leader"] = "张三",
            ["issueDate"] = new DateTimeOffset(2026, 6, 28, 0, 0, 0, TimeSpan.Zero),
            ["qr"] = "https://verify.example.com/CERT-0001",
            ["certificateNo"] = "CERT-0001",
        };

        using var stream = await RenderAsync(def, data);

        ShouldBeValidPdf(stream);
    }

    [Theory]
    [InlineData(PaperKind.A4, CertificateOrientation.Portrait)]
    [InlineData(PaperKind.A4, CertificateOrientation.Landscape)]
    [InlineData(PaperKind.A5, CertificateOrientation.Portrait)]
    [InlineData(PaperKind.Letter, CertificateOrientation.Landscape)]
    public async Task RenderAsync_SupportsAllPaperAndOrientationCombos(
        PaperKind paper, CertificateOrientation orientation)
    {
        var def = new CertificateDefinition("纸张测试", paper, orientation,
            [new TitleElement("title", "X")]);

        using var stream = await RenderAsync(def);

        ShouldBeValidPdf(stream);
    }

    [Fact]
    public async Task RenderAsync_QrCode_RejectsDangerousScheme()
    {
        // 渲染器在替换 {certificateNo} 占位符后再次校验 scheme —— javascript: 应被拒。
        var def = new CertificateDefinition("二维码测试", PaperKind.A4, CertificateOrientation.Portrait,
            [new QrCodeElement("qr", "javascript:alert(1)")]);
        var data = new Dictionary<string, object?> { ["certificateNo"] = "CERT-0001" };

        var act = () => RenderAsync(def, data);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RenderAsync_QrCode_AcceptsHttpsUrl()
    {
        var def = new CertificateDefinition("二维码测试", PaperKind.A4, CertificateOrientation.Portrait,
            [new QrCodeElement("qr", "https://verify.example.com/{certificateNo}")]);
        var data = new Dictionary<string, object?> { ["certificateNo"] = "CERT-0001" };

        using var stream = await RenderAsync(def, data);

        ShouldBeValidPdf(stream);
    }

    [Fact]
    public async Task RenderAsync_QrCodeWithData_SkipsWhenNoUrlResolved()
    {
        // QrCodeElement 无占位符 + data 无对应 key → 不画二维码，渲染仍成功。
        var def = new CertificateDefinition("无二维码", PaperKind.A4, CertificateOrientation.Portrait,
            [new QrCodeElement("qr"), new TitleElement("title", "X")]);

        using var stream = await RenderAsync(def);

        ShouldBeValidPdf(stream);
    }

    [Fact]
    public async Task RenderAsync_ImageElement_FromBase64DataUrl_Renders()
    {
        // 1×1 红色 PNG 的 base64（合法 PNG 头）。
        const string pngBase64 =
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg==";
        var def = new CertificateDefinition("图片测试", PaperKind.A4, CertificateOrientation.Portrait,
            [new ImageElement("img", 50, 50)]);
        var data = new Dictionary<string, object?>
        {
            ["img"] = $"data:image/png;base64,{pngBase64}",
        };

        using var stream = await RenderAsync(def, data);

        ShouldBeValidPdf(stream);
    }

    [Fact]
    public async Task RenderAsync_EmptyElements_ProducesValidPdf()
    {
        var def = new CertificateDefinition("空模板", PaperKind.A4, CertificateOrientation.Portrait, []);

        using var stream = await RenderAsync(def);

        ShouldBeValidPdf(stream);
    }

    [Fact]
    public async Task RenderAsync_CustomStyles_AppliedWithoutThrowing()
    {
        var styles = new CertificateStyles
        {
            TitleFontSize = 36,
            BodyFontSize = 12,
            TextColor = "#0000FF",
            TitleColor = "#FF0000",
            LineColor = "#CCCCCC",
            Margin = 40,
        };
        var def = new CertificateDefinition("样式测试", PaperKind.A4, CertificateOrientation.Portrait,
            [new TitleElement("title", "X"), new LineElement("line")], styles);

        using var stream = await RenderAsync(def);

        ShouldBeValidPdf(stream);
    }
}
