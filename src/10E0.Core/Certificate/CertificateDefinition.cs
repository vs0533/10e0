using System.Text.Json.Serialization;

namespace TenE0.Core.Certificate;

/// <summary>
/// 证书模板定义（issue #185 模板 DSL 的根）。
///
/// <para>
/// 一个 <see cref="CertificateDefinition"/> 描述「证书长什么样 + 数据从哪来」：纸张 / 方向 / 元素列表 / 全局样式。
/// 它是结构化对象（非脚本/非用户输入字符串），由业务方在代码里定义或从 <c>TenE0CertificateTemplate.TemplateJson</c>
/// 反序列化得到。
/// </para>
///
/// <para>
/// <b>序列化</b>：用 <c>System.Text.Json</c> + 多态 <c>[JsonPolymorphic]</c>。元素子类型由
/// <see cref="CertificateElement"/> 上的 <c>[JsonDerivedType]</c> 声明 discriminant，
/// 反序列化时自动回填正确子类型。<see cref="Styles"/> 缺省时序列化为 <c>null</c>（渲染器回退默认）。
/// </para>
///
/// <para>
/// <b>不可变性</b>：<c>record</c> 提供值语义；元素列表用 <c>IReadOnlyList</c> 防外部修改。
/// 模板变更走「改 <c>TemplateJson</c> 字符串 + 重新反序列化」，不直接 mutate 本对象。
/// </para>
/// </summary>
/// <param name="Title">模板标题（渲染快照源，也是 <c>TenE0Certificate.Title</c> 的默认值）。</param>
/// <param name="PaperKind">纸张规格（A4/A5/Letter）。</param>
/// <param name="Orientation">页面方向（纵向/横向）。证书最常用横向。</param>
/// <param name="Elements">版式元素列表，渲染顺序 = 数组顺序（自上而下）。</param>
/// <param name="Styles">全局样式；null 用渲染器内置默认（见 <see cref="CertificateStyles"/>）。</param>
public sealed record CertificateDefinition(
    string Title,
    [property: JsonPropertyName("paperKind")] PaperKind PaperKind,
    [property: JsonPropertyName("orientation")] CertificateOrientation Orientation,
    IReadOnlyList<CertificateElement> Elements,
    CertificateStyles? Styles = null);
