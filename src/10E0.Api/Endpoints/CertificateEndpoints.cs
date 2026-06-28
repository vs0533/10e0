using Asp.Versioning;
using TenE0.Api.Handlers;
using TenE0.Core.Abstractions;
using TenE0.Core.Certificate;
using TenE0.Core.Common;
using TenE0.Core.Errors;

namespace TenE0.Api.Endpoints;

/// <summary>
/// #185 证书 demo 端点。
///
/// <para>
/// 范本用法：POST 渲染（模板 code + 数据字典 → PDF 落库 + 证书实例），
/// GET 按业务实体查询已生成证书。证书不走 IEntityService（非聚合），直接 ICertificateService。
/// </para>
/// </summary>
internal static class CertificateEndpoints
{
    public static WebApplication MapCertificateEndpoints(this WebApplication app)
    {
        var versions = app.NewApiVersionSet()
            .HasApiVersion(new ApiVersion(1, 0))
            .ReportApiVersions()
            .Build();

        // 渲染证书：传模板 code + 数据字典 + 可选渲染元数据 → 返回证书实例（含 FileAttachmentId 下载入口）。
        app.MapPost("/demo/certificates/render", async (
            RenderCertificateDto dto,
            ICommandDispatcher dispatcher,
            IErrs errs,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(dto.TemplateCode))
            {
                errs.Add("模板编码不能为空", "templateCode", "TEMPLATE_CODE_REQUIRED");
                return ApiResultResult.Api(ApiResult<object>.FromErrs(errs));
            }

            var options = new CertificateRenderOptions(
                CertificateNo: dto.CertificateNo,
                RelatedEntityId: dto.RelatedEntityId,
                RelatedEntityType: dto.RelatedEntityType);

            var cert = await dispatcher.SendAsync(
                new RenderCertificateCommand(dto.TemplateCode, dto.Data, options), ct);

            return errs.IsValid
                ? ApiResultResult.Api(ApiResult<object>.Ok(cert))
                : ApiResultResult.Api(ApiResult<object>.FromErrs(errs));
        }).WithApiVersionSet(versions).HasApiVersion(new ApiVersion(1, 0))
          .WithName("RenderCertificate")
          .WithDescription("渲染证书（模板 code + 数据 → PDF 落库 + 证书实例）");

        // 按业务实体查询已生成证书：GET /demo/certificates/by-entity/{type}/{id}
        app.MapGet("/demo/certificates/by-entity/{type}/{id}", async (
            string type,
            string id,
            ICommandDispatcher dispatcher,
            CancellationToken ct) =>
        {
            var list = await dispatcher.SendAsync(
                new ListCertificatesByEntityQuery(type, id), ct);
            return ApiResultResult.Api(ApiResult<object>.Ok(list));
        }).WithApiVersionSet(versions).HasApiVersion(new ApiVersion(1, 0))
          .WithName("ListCertificatesByEntity")
          .WithDescription("按业务实体查询已生成证书");

        return app;
    }
}
