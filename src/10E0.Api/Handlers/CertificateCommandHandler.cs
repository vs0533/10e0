using TenE0.Core.Abstractions;
using TenE0.Core.Certificate;

namespace TenE0.Api.Handlers;

/// <summary>
/// #185 证书 demo Handler。
///
/// <para>
/// 证书不是聚合（无业务方法 / 无领域事件 / 无需唯一性校验链），故<b>不走</b> <c>IEntityService</c>，
/// 直接走 <see cref="ICertificateService"/> —— 它已封装"模板加载 → Sequence 编号 → PDF 渲染 → IFileService 落库 → 写证书实例"全流程。
/// Handler 仅做实体 → <see cref="CertificateView"/> 投影。
/// </para>
/// </summary>
internal sealed class RenderCertificateHandler(ICertificateService certificateSvc)
    : ICommandHandler<RenderCertificateCommand, CertificateView>
{
    public async Task<CertificateView> HandleAsync(RenderCertificateCommand cmd, CancellationToken ct)
    {
        var cert = await certificateSvc.RenderAsync(cmd.TemplateCode, cmd.Data, cmd.Options, ct);
        return new CertificateView(
            cert.Id, cert.TemplateCode, cert.Title, cert.CertificateNo,
            cert.FileAttachmentId, cert.RelatedEntityId, cert.RelatedEntityType, cert.CreateTime);
    }
}

internal sealed class ListCertificatesByEntityHandler(ICertificateService certificateSvc)
    : ICommandHandler<ListCertificatesByEntityQuery, List<CertificateView>>
{
    public async Task<List<CertificateView>> HandleAsync(ListCertificatesByEntityQuery query, CancellationToken ct)
    {
        var list = await certificateSvc.GetByRelatedEntityAsync(query.RelatedEntityType, query.RelatedEntityId, ct);
        return list.Select(c => new CertificateView(
            c.Id, c.TemplateCode, c.Title, c.CertificateNo,
            c.FileAttachmentId, c.RelatedEntityId, c.RelatedEntityType, c.CreateTime)).ToList();
    }
}
