using System.Text.Json;
using TenE0.Core.Certificate;

namespace TenE0.Core.Tests.Certificates;

/// <summary>
/// 证书模板 DSL 单元测试（issue #185）：
/// 序列化/反序列化往返、各元素类型构造、数据绑定占位符匹配、URL scheme 白名单。
/// </summary>
[Trait("Category", "Unit")]
public sealed class CertificateDefinitionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void CertificateDefinition_Construct_WithAllElementTypes()
    {
        // 验证所有 9 种元素类型可构造，Key 正确携带。
        CertificateElement[] elements =
        [
            new TitleElement("title", "标题"),
            new TextElement("no", "项目编号"),
            new NameElement("leader"),
            new DateElement("date"),
            new QrCodeElement("qr", "https://verify.example.com/{certificateNo}"),
            new ImageElement("img"),
            new SealElement("seal", "签发单位"),
            new SignatureElement("sig"),
            new LineElement("line"),
        ];

        var def = new CertificateDefinition("结业证书", PaperKind.A4, CertificateOrientation.Landscape, elements);

        def.Title.Should().Be("结业证书");
        def.PaperKind.Should().Be(PaperKind.A4);
        def.Orientation.Should().Be(CertificateOrientation.Landscape);
        def.Elements.Should().HaveCount(9);
        def.Elements.OfType<TitleElement>().Single().Text.Should().Be("标题");
        def.Elements.OfType<QrCodeElement>().Single().UrlPlaceholder.Should().Contain("{certificateNo}");
    }

    [Fact]
    public void CertificateDefinition_SerializeDeserialize_Roundtrip_PreservesElementTypes()
    {
        // 验证多态序列化：TemplateJson 存的是 JSON，反序列化回正确子类型（discriminant 由 [JsonDerivedType] 声明）。
        var def = new CertificateDefinition("结业证书", PaperKind.A5, CertificateOrientation.Portrait,
        [
            new TitleElement("title", "标题"),
            new NameElement("leader"),
            new QrCodeElement("qr", "https://verify.example.com/{certificateNo}"),
            new LineElement("line", "#FF0000"),
        ]);

        var json = JsonSerializer.Serialize(def, JsonOptions);
        var back = JsonSerializer.Deserialize<CertificateDefinition>(json, JsonOptions);

        back.Should().NotBeNull();
        back!.Title.Should().Be("结业证书");
        back.PaperKind.Should().Be(PaperKind.A5);
        back.Orientation.Should().Be(CertificateOrientation.Portrait);
        // 关键：子类型正确还原（非基类 CertificateElement）。
        back.Elements[0].Should().BeOfType<TitleElement>().Which.Text.Should().Be("标题");
        back.Elements[1].Should().BeOfType<NameElement>().Which.Label.Should().Be("姓名");
        back.Elements[2].Should().BeOfType<QrCodeElement>().Which.UrlPlaceholder.Should().Contain("{certificateNo}");
        back.Elements[3].Should().BeOfType<LineElement>().Which.Color.Should().Be("#FF0000");
    }

    [Theory]
    [InlineData("http://verify.example.com/abc", true)]
    [InlineData("https://verify.example.com/abc", true)]
    [InlineData("HTTPS://verify.example.com/abc", true)] // 大小写不敏感
    [InlineData("javascript:alert(1)", false)]            // 危险 scheme 拒绝
    [InlineData("file:///etc/passwd", false)]
    [InlineData("data:text/html,<script>", false)]
    public void ValidateUrlScheme_OnlyAllowsHttpHttps(string url, bool shouldPass)
    {
        // issue 安全考量 #3：URL scheme 白名单。
        var act = () => DataBinder.ValidateUrlScheme(url);
        if (shouldPass)
            act.Should().NotThrow("'{0}' 是合法的 http/https URL", url);
        else
            act.Should().Throw<ArgumentException>("'{0}' 含危险 scheme，应被拒绝", url);
    }

    [Fact]
    public void ValidateUrlScheme_NullOrEmpty_PassesThrough()
    {
        // null / 空 URL 放行（渲染器决定是否跳过二维码）。
        DataBinder.ValidateUrlScheme(null).Should().BeEmpty();
        DataBinder.ValidateUrlScheme("").Should().BeEmpty();
    }

    [Fact]
    public void DataBinder_Resolve_Title_UsesDataWhenKeyMatches()
    {
        // 命中字典用字典值，否则用元素自带 Text。
        var title = new TitleElement("title", "默认标题");
        var withData = new Dictionary<string, object?> { ["title"] = "动态标题" };
        var withoutData = new Dictionary<string, object?>();

        DataBinder.Resolve(title, withData).Should().Be("动态标题");
        DataBinder.Resolve(title, withoutData).Should().Be("默认标题");
    }

    [Fact]
    public void DataBinder_Resolve_Date_FormatsByElementFormat()
    {
        var date = new DateElement("date", "签发日期", "yyyy/MM/dd");
        var data = new Dictionary<string, object?>
        {
            ["date"] = new DateTimeOffset(2026, 6, 28, 0, 0, 0, TimeSpan.Zero),
        };

        DataBinder.Resolve(date, data).Should().Be("签发日期：2026/06/28");
    }

    [Fact]
    public void DataBinder_Resolve_QrCode_UsesDataOverPlaceholder()
    {
        // data 命中用 data 值；含占位符时跳过 scheme 校验（由 CertificateService 替换后再校验）。
        var qr = new QrCodeElement("qr", "https://default.example.com/{certificateNo}");
        var withData = new Dictionary<string, object?> { ["qr"] = "https://override.example.com/xyz" };
        var withoutData = new Dictionary<string, object?>();

        DataBinder.Resolve(qr, withData).Should().Be("https://override.example.com/xyz");
        DataBinder.Resolve(qr, withoutData).Should().Contain("default.example.com");
    }

    [Fact]
    public void DataBinder_Resolve_QrCode_RejectsDangerousSchemeFromData()
    {
        // data 里塞 javascript: URL —— Resolve 应抛（DataBinder.ResolveQrUrl 校验非占位符 URL）。
        var qr = new QrCodeElement("qr");
        var data = new Dictionary<string, object?> { ["qr"] = "javascript:alert(1)" };

        var act = () => DataBinder.Resolve(qr, data);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DataBinder_Resolve_QrCode_AllowsPlaceholderUrl_BypassingStrictCheck()
    {
        // 含 {certificateNo} 占位符的 URL 不做严格校验（占位符未替换前无法判定最终 scheme，
        // 但 https://...{certificateNo} 模板本身 scheme 已合法）。CertificateService 替换后再校验。
        var qr = new QrCodeElement("qr", "https://verify.example.com/{certificateNo}");
        var data = new Dictionary<string, object?>();

        var act = () => DataBinder.Resolve(qr, data);
        act.Should().NotThrow();
    }
}
