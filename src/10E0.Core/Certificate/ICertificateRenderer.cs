namespace TenE0.Core.Certificate;

/// <summary>
/// 证书渲染器抽象（issue #185）。
///
/// <para>
/// 把 <see cref="CertificateDefinition"/> + 绑定后的数据字典渲染为目标格式的文档流。
/// 默认实现 <c>PdfCertificateRenderer</c>（在独立 NuGet 包 <c>TenE0.Core.Certificate</c>，基于 PDFsharp）。
/// </para>
///
/// <para>
/// <b>可替换</b>：业务方可自定义实现（图片渲染器 / Word 渲染器 / SVG 渲染器），通过
/// <c>services.Replace(ServiceDescriptor.Scoped&lt;ICertificateRenderer, YourRenderer&gt;())</c>
/// 覆盖默认注册。主包注册一个 <see cref="NullCertificateRenderer"/> 占位 ——
/// 未引用独立包且未自定义时，调用 Render 抛明确异常（启动不崩，与 RabbitMq Replace 模式一致）。
/// </para>
/// </summary>
public interface ICertificateRenderer
{
    /// <summary>
    /// 渲染器输出的文档格式标识（如 <c>pdf</c> / <c>png</c> / <c>docx</c>）。
    /// <see cref="ICertificateService"/> 据此决定 <c>TenE0FileAttachment.ContentType</c>。
    /// </summary>
    string Format { get; }

    /// <summary>
    /// 渲染证书为文档流。
    /// </summary>
    /// <param name="definition">模板定义（纸张 / 方向 / 元素 / 样式）。</param>
    /// <param name="data">已绑定的数据字典（元素 Key → 值）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>渲染产物流（调用方负责 Dispose；通常 MemoryStream）。</returns>
    Task<Stream> RenderAsync(
        CertificateDefinition definition,
        IReadOnlyDictionary<string, object?> data,
        CancellationToken ct = default);
}

/// <summary>
/// 占位渲染器 —— 主包默认注册，未引用独立 PDF 渲染器包且未自定义时使用。
///
/// <para>
/// 调用 <see cref="RenderAsync"/> 抛 <see cref="InvalidOperationException"/>，给出明确的"请引用独立包"指引。
/// 这样让证书模块的"配置 / 模板管理 / 数据绑定"在主包即可工作，只有真正渲染 PDF 时才需要独立包 ——
/// 与 RabbitMq/Kafka 的"主包零重依赖，独立包按需 Replace"模式完全一致。
/// </para>
/// </summary>
internal sealed class NullCertificateRenderer : ICertificateRenderer
{
    /// <inheritdoc />
    public string Format => "null";

    /// <inheritdoc />
    public Task<Stream> RenderAsync(
        CertificateDefinition definition,
        IReadOnlyDictionary<string, object?> data,
        CancellationToken ct = default)
        => throw new InvalidOperationException(
            "未配置证书渲染器。请引用独立 NuGet 包 'TenE0.Core.Certificate'（PDFsharp 默认渲染器）" +
            "并在 Program.cs 调 services.AddTenE0PdfCertificateRenderer()；" +
            "或注册自定义 ICertificateRenderer 实现。主包 'TenE0.Core' 故意不携带 PDF 渲染重依赖。");
}
