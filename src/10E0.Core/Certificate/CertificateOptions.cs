namespace TenE0.Core.Certificate;

/// <summary>
/// Certificate 模块的运行参数（issue #185）。
///
/// <para>
/// 由 <c>AddTenE0Certificate&lt;TContext&gt;(configure)</c> 注册到 <c>IOptions&lt;CertificateOptions&gt;</c>，
/// 运行时由 <see cref="CertificateService{TContext}"/> / <c>PdfCertificateRenderer</c> 读取。
/// </para>
/// </summary>
public sealed class CertificateOptions
{
    /// <summary>
    /// 默认字体（渲染器 fallback）。证书常含中文，默认 <c>Microsoft YaHei</c>（雅黑）。
    /// 运行环境无此字体时，渲染器回退系统默认字体（可能显示为方块，需运维预装中文字体）。
    /// </summary>
    public string DefaultFont { get; set; } = "Microsoft YaHei";

    /// <summary>
    /// 渲染产物在 <c>TenE0FileAttachment.Category</c> 上的标记。默认 <c>certificate</c>。
    /// 业务方可改成更细的分类（如 <c>certificate/research-completion</c>）。
    /// </summary>
    public string StorageCategory { get; set; } = "certificate";

    /// <summary>
    /// 证书编号流水号 key。配 <c>[Sequence]</c> 同款 key；<c>ICertificateService.RenderAsync</c>
    /// 渲染期调 <c>ISequenceGenerator.NextAsync(SequenceKey, ...)</c> 生成证书编号。
    /// 为 null 时不自动生成编号（调用方需在 <c>CertificateRenderOptions.CertificateNo</c> 显式传入，否则留空）。
    /// </summary>
    public string? SequenceKey { get; set; }

    /// <summary>
    /// 证书编号格式串（<c>[Sequence]</c> 同款语法，见 docs/15-sequences.md）。
    /// 仅当 <see cref="SequenceKey"/> 配置时生效。默认 <c>CERT-{yyyyMMdd}-{0000}</c>。
    /// </summary>
    public string SequenceFormat { get; set; } = "CERT-{yyyyMMdd}-{0000}";
}
