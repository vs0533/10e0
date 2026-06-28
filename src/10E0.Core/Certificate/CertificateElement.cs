using System.Text.Json.Serialization;

namespace TenE0.Core.Certificate;

/// <summary>
/// 证书模板的一个版式元素（issue #185 模板 DSL）。
///
/// <para>
/// 这是 <see cref="CertificateDefinition"/> 的叶子节点。每个元素带一个 <see cref="Key"/>，
/// 渲染时与 <c>IReadOnlyDictionary&lt;string, object?&gt;</c> 的同名 key 做数据绑定 ——
/// 命中即用字典值填充元素的 <c>Placeholder</c>/<c>UrlPlaceholder</c>，未命中则用元素自带默认值。
/// </para>
///
/// <para>
/// <b>子类型</b>（覆盖 95% 证书版式需求）见文件下方 9 个 <c>sealed record</c>：
/// 标题 / 文本 / 姓名 / 日期 / 二维码 / 图片 / 盖章位 / 签字位 / 分隔线。
/// 新增版式能力时在此追加子类型即可，渲染器（默认 PdfCertificateRenderer）按多态分支处理。
/// </para>
///
/// <para>
/// <b>序列化</b>：<c>TemplateJson</c> 用 <c>System.Text.Json</c> + 多态 <c>[JsonDerivedType]</c>
/// 标注（.NET 7+），反序列化回正确的子类型。详见 <see cref="CertificateDefinition"/>。
/// </para>
/// </summary>
/// <param name="Key">数据绑定键。渲染时与 <c>data</c> 字典同名 key 匹配。</param>
[JsonDerivedType(typeof(TitleElement), "title")]
[JsonDerivedType(typeof(TextElement), "text")]
[JsonDerivedType(typeof(NameElement), "name")]
[JsonDerivedType(typeof(DateElement), "date")]
[JsonDerivedType(typeof(QrCodeElement), "qrcode")]
[JsonDerivedType(typeof(ImageElement), "image")]
[JsonDerivedType(typeof(SealElement), "seal")]
[JsonDerivedType(typeof(SignatureElement), "signature")]
[JsonDerivedType(typeof(LineElement), "line")]
public abstract record CertificateElement(string Key);

/// <summary>
/// 标题元素 —— 证书顶部大字标题（如"科研项目结题证书"）。通常居中、最大字号。
/// </summary>
/// <param name="Key">绑定键。</param>
/// <param name="Text">标题文本。<c>data</c> 字典命中同名 key 时用字典值覆盖（动态标题）。</param>
public sealed record TitleElement(string Key, string Text) : CertificateElement(Key);

/// <summary>
/// 通用文本元素 —— 一行带标签的文本（如"项目编号：PRD-001"）。
/// </summary>
/// <param name="Key">绑定键。</param>
/// <param name="Label">行首标签（如"项目编号"）。</param>
/// <param name="Placeholder">默认占位值；<c>data</c> 字典命中时被覆盖。null 则仅显示 Label + 绑定值。</param>
public sealed record TextElement(string Key, string Label, string? Placeholder = null) : CertificateElement(Key);

/// <summary>
/// 姓名元素 —— 突出显示的姓名字段（如负责人姓名）。渲染时字号通常比正文大、加粗或带下划线。
/// </summary>
/// <param name="Key">绑定键。</param>
/// <param name="Label">行首标签（默认"姓名"）。</param>
public sealed record NameElement(string Key, string Label = "姓名") : CertificateElement(Key);

/// <summary>
/// 日期元素 —— 签发日期。绑定值按 <see cref="Format"/> 格式化（默认 yyyy-MM-dd）。
/// </summary>
/// <param name="Key">绑定键。</param>
/// <param name="Label">行首标签（默认"签发日期"）。</param>
/// <param name="Format">日期格式串，默认 <c>yyyy-MM-dd</c>（与 <c>DateTimeOffset.ToString(string)</c> 一致）。</param>
public sealed record DateElement(string Key, string Label = "签发日期", string Format = "yyyy-MM-dd") : CertificateElement(Key);

/// <summary>
/// 二维码元素 —— 渲染一个二维码（典型用法：扫码验真）。
///
/// <para>
/// <b>安全</b>：<see cref="UrlPlaceholder"/> / 绑定的 URL 渲染前经 <see cref="DataBinder"/>
/// 校验 scheme 白名单（仅 http/https）—— 拒绝 <c>javascript:</c> / <c>file:</c> 等危险 scheme，
/// 防止恶意 URL 被编码进二维码后由扫码终端触发非预期行为（issue 安全考量 #3）。
/// </para>
/// </summary>
/// <param name="Key">绑定键。</param>
/// <param name="UrlPlaceholder">默认 URL 模板。支持 <c>{certificateNo}</c> 占位符（渲染期替换为实际证书编号）。</param>
public sealed record QrCodeElement(string Key, string? UrlPlaceholder = null) : CertificateElement(Key);

/// <summary>
/// 图片元素 —— 嵌入一张图片（如徽标 / 头像）。绑定值应为图片字节数组的 base64 字符串或图片 URL。
/// </summary>
/// <param name="Key">绑定键。</param>
/// <param name="Width">渲染宽度（pt，1pt ≈ 0.353mm）。默认 100。</param>
/// <param name="Height">渲染高度（pt）。默认 100。</param>
public sealed record ImageElement(string Key, double Width = 100, double Height = 100) : CertificateElement(Key);

/// <summary>
/// 盖章位 —— 签发单位的盖章位置占位（渲染为一个带边框的方框 + 单位名）。
/// 实际印章图片由线下补盖，本元素只负责版式定位。
/// </summary>
/// <param name="Key">绑定键。</param>
/// <param name="Label">单位名（如"淄博市卫生健康委员会"）。</param>
public sealed record SealElement(string Key, string Label = "签发单位") : CertificateElement(Key);

/// <summary>
/// 签字位 —— 负责人签字位置占位（渲染为一根下划线 + 标签）。
/// </summary>
/// <param name="Key">绑定键。</param>
/// <param name="Label">签字标签（默认"签字"）。</param>
public sealed record SignatureElement(string Key, string Label = "签字") : CertificateElement(Key);

/// <summary>
/// 分隔线元素 —— 一条水平线，用于版式分段（如标题与正文之间）。
/// </summary>
/// <param name="Key">绑定键。</param>
/// <param name="Color">线条颜色（CSS 颜色名或十六进制 <c>#RRGGBB</c>）。null 用样式默认。</param>
public sealed record LineElement(string Key, string? Color = null) : CertificateElement(Key);
