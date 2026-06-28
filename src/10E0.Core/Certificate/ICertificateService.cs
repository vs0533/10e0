using TenE0.Core.Certificate.Entities;

namespace TenE0.Core.Certificate;

/// <summary>
/// 证书服务（issue #185）—— 把模板 + 数据渲染为证书文档，并可选持久化为可追溯的证书实例。
///
/// <para>
/// <b>三职责</b>：
/// <list type="bullet">
/// <item><see cref="RenderAsync"/>：渲染 + 落库（PDF 存 IFileService + 写 TenE0Certificate 实例），返回证书实例。</item>
/// <item><see cref="RenderToStreamAsync"/>：仅渲染到流（不落库），用于即时预览 / 下载。</item>
/// <item><see cref="GetByRelatedEntityAsync"/>：按业务实体查询已生成证书。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>与 IFileService 集成</b>：<see cref="RenderAsync"/> 把渲染产物 PDF 通过 <c>IFileService.UploadAsync</c>
/// 存储，复用 <c>TenE0FileAttachment</c>（<c>Category</c>=证书分类、<c>RelatedEntityId</c>=业务实体 Id）。
/// 证书模块依赖 Files 模块（<c>opt.Files = true</c>），装配期校验。
/// </para>
///
/// <para>
/// <b>编号生成</b>：见 <see cref="CertificateOptions.SequenceKey"/> 与 <see cref="CertificateRenderOptions.CertificateNo"/>。
/// </para>
/// </summary>
public interface ICertificateService
{
    /// <summary>
    /// 渲染证书并落库：PDF 存 IFileService + 写证书实例 + 可选走 Sequence 生成编号。返回证书实例。
    /// </summary>
    /// <param name="templateCode">模板业务编码（<see cref="TenE0CertificateTemplate.Code"/>）。模板须存在且启用。</param>
    /// <param name="data">数据字典（元素 Key → 值）。值仅作字符串/数字/日期/图片占位符替换，不解析为表达式。</param>
    /// <param name="options">渲染元数据（关联实体 / 编号来源 / 存储分类）。null 用默认。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已持久化的证书实例（<see cref="TenE0Certificate.FileAttachmentId"/> 指向 PDF 文件）。</returns>
    Task<TenE0Certificate> RenderAsync(
        string templateCode,
        IReadOnlyDictionary<string, object?> data,
        CertificateRenderOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// 仅渲染到流，不落库（用于即时预览 / 下载）。
    /// </summary>
    Task<Stream> RenderToStreamAsync(
        string templateCode,
        IReadOnlyDictionary<string, object?> data,
        CancellationToken ct = default);

    /// <summary>
    /// 按业务实体查询已生成的证书列表（如"某项目下的所有结题证书"）。
    /// </summary>
    Task<List<TenE0Certificate>> GetByRelatedEntityAsync(
        string relatedEntityType, string relatedEntityId, CancellationToken ct = default);
}
