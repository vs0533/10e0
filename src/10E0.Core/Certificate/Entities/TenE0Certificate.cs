using TenE0.Core.Abstractions;
using TenE0.Core.Entities;

namespace TenE0.Core.Certificate.Entities;

/// <summary>
/// 证书实例（issue #185）—— 一次具体的证书生成，可追溯 / 可重生成。
///
/// <para>
/// 每行 = 一次 <c>ICertificateService.RenderAsync</c> 调用的产物。渲染流程：
/// 加载模板 → 序列化快照 → 走 Sequence 生成 <see cref="CertificateNo"/> → 渲染为 PDF →
/// <c>IFileService.UploadAsync</c> 落库得到 <see cref="FileAttachmentId"/> → 写入本实体。
/// </para>
///
/// <para>
/// <b>快照字段</b>（<see cref="Title"/> / <see cref="CertificateNo"/> / <see cref="DataJson"/>）：
/// 即使后续模板或数据源变更，已生成的证书仍保留生成当时的快照，满足审计 / 法律追溯需求。
/// </para>
///
/// <para>
/// <b>关联业务实体</b>（<see cref="RelatedEntityId"/> / <see cref="RelatedEntityType"/>）：
/// 让业务方按"某个项目下的所有证书"查询（<c>GetByRelatedEntityAsync</c>），典型场景：
/// 科研项目结题后查它的结题证书。
/// </para>
///
/// <para>
/// <b>租户隔离</b>：实现 <see cref="IMultiTenantEntity"/>，自动走租户 Named Query Filter。
/// </para>
/// </summary>
public sealed class TenE0Certificate : AuditedEntity, IMultiTenantEntity
{
    /// <summary>对应模板的业务编码（<see cref="TenE0CertificateTemplate.Code"/>）。冗余字段便于查询。</summary>
    public string TemplateCode { get; set; } = string.Empty;

    /// <summary>标题快照（生成当时的标题，来自模板 <see cref="CertificateDefinition.Title"/>）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 证书编号（唯一）。可由 <c>[Sequence]</c> 自动生成（<c>CertificateOptions.SequenceKey</c> 配置时），
    /// 或调用方在 <c>CertificateRenderOptions</c> 显式传入。
    /// </summary>
    public string CertificateNo { get; set; } = string.Empty;

    /// <summary>生成时绑定的数据字典快照（JSON）。保留以供追溯 / 重生成。</summary>
    public string DataJson { get; set; } = string.Empty;

    /// <summary>
    /// 指向 <c>TenE0FileAttachment.Id</c>（渲染产物的 PDF 文件元数据）。null 表示渲染产物未落库
    /// （仅 <c>RenderToStreamAsync</c> 路径会出现 null）。
    /// </summary>
    public string? FileAttachmentId { get; set; }

    /// <summary>关联业务实体 Id（如科研项目 Id）。null 表示独立证书。</summary>
    public string? RelatedEntityId { get; set; }

    /// <summary>关联业务实体类型名（如 <c>ResearchProject</c>）。</summary>
    public string? RelatedEntityType { get; set; }

    /// <inheritdoc />
    public string TenantId { get; set; } = string.Empty;
}
