namespace TenE0.Core.Certificate;

/// <summary>
/// 证书渲染请求的附加选项（issue #185）。
///
/// <para>
/// 与 <see cref="ICertificateService.RenderAsync"/> 的 <c>data</c> 字典正交：
/// <c>data</c> 是<b>模板内容</b>的动态数据（姓名 / 编号 / 日期 ...），
/// 本对象是<b>这次渲染行为</b>的元数据（关联实体 / 编号来源 / 存储分类）。
/// </para>
/// </summary>
/// <param name="CertificateNo">
/// 显式指定证书编号。不传（null）则：若 <see cref="CertificateOptions.SequenceKey"/> 配置了，
/// 走 Sequence 自动生成；否则留空。业务方手动传入时需自行保证唯一（表上有唯一索引兜底）。
/// </param>
/// <param name="RelatedEntityId">关联业务实体 Id（如科研项目 Id）。null = 独立证书。</param>
/// <param name="RelatedEntityType">关联业务实体类型名（如 <c>ResearchProject</c>）。建议与实体类名一致。</param>
/// <param name="Category">
/// 存储分类。覆盖 <see cref="CertificateOptions.StorageCategory"/>；默认 <c>certificate</c>。
/// 写入 <c>TenE0FileAttachment.Category</c>，便于按分类查询证书 PDF。
/// </param>
public sealed record CertificateRenderOptions(
    string? CertificateNo = null,
    string? RelatedEntityId = null,
    string? RelatedEntityType = null,
    string? Category = "certificate");
