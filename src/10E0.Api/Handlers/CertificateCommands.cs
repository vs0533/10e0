using TenE0.Api.Domain;
using TenE0.Core.Abstractions;
using TenE0.Core.Certificate;
using TenE0.Core.Permissions;

namespace TenE0.Api.Handlers;

// DTOs（证书 demo，issue #185）
internal sealed record RenderCertificateDto(
    string TemplateCode,
    Dictionary<string, object?> Data,
    string? CertificateNo,
    string? RelatedEntityId,
    string? RelatedEntityType);

internal sealed record CertificateView(
    string Id,
    string TemplateCode,
    string Title,
    string CertificateNo,
    string? FileAttachmentId,
    string? RelatedEntityId,
    string? RelatedEntityType,
    DateTimeOffset? CreateTime);

// Queries / Commands
// #185：证书不是聚合（无业务方法 / 无领域事件），直接走 ICertificateService（不走 IEntityService）。
[RequirePermission(DemoPermissions.CertificateView)]
internal sealed record ListCertificatesByEntityQuery(string RelatedEntityType, string RelatedEntityId)
    : IQuery<List<CertificateView>>;

[RequirePermission(DemoPermissions.CertificateRender)]
internal sealed record RenderCertificateCommand(
    string TemplateCode,
    IReadOnlyDictionary<string, object?> Data,
    CertificateRenderOptions? Options) : ICommand<CertificateView>;
