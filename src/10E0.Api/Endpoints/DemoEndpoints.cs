using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TenE0.Api.Domain;
using TenE0.Api.Handlers;
using TenE0.Core.Abstractions;
using TenE0.Core.Common;
using TenE0.Core.EntityService;
using TenE0.Core.Errors;
using TenE0.Core.ImportExport;
using TenE0.Core.Json;
using TenE0.Core.Queries;

namespace TenE0.Api.Endpoints;

internal static class DemoEndpoints
{
    public static WebApplication MapDemoEndpoints(this WebApplication app)
    {
        // #163：Demo 端点的版本集合。核心 CRUD 端点声明 v1.0；
        // ReportApiVersions 让响应头返回 api-supported-versions，客户端可探测升级路径。
        // 辅助端点（whoami / 导入导出 / 模板 / 动态查询 / posted-props / partial）本期不加版本化，
        // 保持示范简单 —— 业务端点按需声明版本即可。
        var versions = app.NewApiVersionSet()
            .HasApiVersion(new ApiVersion(1, 0))
            .ReportApiVersions()
            .Build();

        app.MapGet("/whoami", (ICurrentUserContext user) => new
        {
            user = user.UserCode,
            authenticated = user.IsAuthenticated,
            roles = user.RoleIds,
        }).WithApiVersionSet(versions).HasApiVersion(new ApiVersion(1, 0));

        // #39: 不再内联 try/catch PermissionDeniedException。
        // 所有 domain 异常由 TenE0ExceptionHandler 集中映射为 ApiResult<T> 形状。
        app.MapPost("/demo", async (CreateDemoDto dto, ICommandDispatcher dispatcher, IErrs errs, CancellationToken ct) =>
        {
            var id = await dispatcher.SendAsync(new CreateDemoCommand(dto.Name, dto.OrgId, dto.Salary), ct);
            return errs.IsValid
                ? ApiResultResult.Api(ApiResult<object>.Ok(new { id }))
                : ApiResultResult.Api(ApiResult<object>.FromErrs(errs));
        }).WithApiVersionSet(versions).HasApiVersion(new ApiVersion(1, 0));

        app.MapPut("/demo/{id}", async (string id, UpdateDemoDto dto, ICommandDispatcher dispatcher, IErrs errs, CancellationToken ct) =>
        {
            var ok = await dispatcher.SendAsync(new UpdateDemoCommand(id, dto.Name, dto.Salary), ct);
            return ok && errs.IsValid
                ? ApiResultResult.Api(ApiResult<object>.Ok(new { ok = true }))
                : ApiResultResult.Api(ApiResult<object>.FromErrs(errs));
        }).WithApiVersionSet(versions).HasApiVersion(new ApiVersion(1, 0));

        app.MapDelete("/demo/{id}", async (string id, ICommandDispatcher dispatcher, CancellationToken ct) =>
        {
            var ok = await dispatcher.SendAsync(new DeleteDemoCommand(id), ct);
            return ApiResultResult.Api(ApiResult<object>.Ok(new { ok }));
        }).WithApiVersionSet(versions).HasApiVersion(new ApiVersion(1, 0));

        app.MapGet("/demo", async (ICommandDispatcher dispatcher, CancellationToken ct) =>
        {
            var list = await dispatcher.SendAsync(new ListDemosQuery(), ct);
            return ApiResultResult.Api(ApiResult<object>.Ok(list));
        }).WithApiVersionSet(versions).HasApiVersion(new ApiVersion(1, 0));

        // #184 范本:分页查询端点,走 IEntityQueryService(读侧对称服务)。
        // 路径参数从 query string 绑定(PagedQuery 是 record,ASP.NET 自动 [AsParameters])。
        app.MapGet("/demo/paged", async (
            ICommandDispatcher dispatcher,
            string? name,
            [AsParameters] PagedQuery paged,
            CancellationToken ct) =>
        {
            var result = await dispatcher.SendAsync(new PagedDemosQuery(paged, name), ct);
            return ApiResultResult.Api(ApiResult<object>.Ok(result));
        }).WithApiVersionSet(versions).HasApiVersion(new ApiVersion(1, 0));

        app.MapPost("/demo/{id}/publish", async (string id, ICommandDispatcher dispatcher, IErrs errs, CancellationToken ct) =>
        {
            var ok = await dispatcher.SendAsync(new PublishDemoCommand(id), ct);
            return ok && errs.IsValid
                ? ApiResultResult.Api(ApiResult<object>.Ok(new { ok = true }))
                : ApiResultResult.Api(ApiResult<object>.FromErrs(errs));
        }).WithApiVersionSet(versions).HasApiVersion(new ApiVersion(1, 0));

        // 动态查询演示
        app.MapGet("/demo/query", async (IDbContextFactory<DemoDbContext> f, [AsParameters] PagedQuery query, CancellationToken ct) =>
        {
            using var ctx = await f.CreateDbContextAsync(ct);
            var q = ctx.Set<DemoEntity>().AsNoTracking().AsQueryable();

            // 动态 WHERE
            if (!string.IsNullOrWhiteSpace(query.Where))
                q = q.DynamicWhere(query.Where);

            // 动态 ORDER BY
            q = q.DynamicOrderBy(query.OrderBy ?? "CreateTime desc");

            // 统计总数
            var total = await q.CountAsync(ct);

            // 分页
            var items = await q.Page(query.Page, query.PageSize).ToListAsync(ct);

            return Results.Ok(PagedResult<DemoEntity>.Create(items, total, query.Page, query.PageSize));
        }).WithApiVersionSet(versions).HasApiVersion(new ApiVersion(1, 0));

        // ─── 导入导出（issue #154）演示 ───────────────────────
        // 导出：接 DynamicWhere/OrderBy，走 IExcelExporter；超阈值自动降级 CSV。
        app.MapGet("/demo/export", async (
            HttpContext http,
            IDbContextFactory<DemoDbContext> f,
            IExcelExporter exporter,
            [AsParameters] PagedQuery query,
            CancellationToken ct) =>
        {
            using var ctx = await f.CreateDbContextAsync(ct);
            var q = ctx.Set<DemoEntity>().AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(query.Where))
                q = q.DynamicWhere(query.Where);
            q = q.DynamicOrderBy(query.OrderBy ?? "CreateTime desc");

            // 导出不限分页，但受 ImportExportOptions.MaxExportRows 兜底
            var export = await exporter.ExportAsync(q, new ExportOptions { SheetName = "Demo列表" }, ct);

            // ExportStream 持有 MemoryStream 所有权 —— Results.File 不会释放传入的 Stream，
            // 注册到请求生命周期确保响应结束后释放，避免每次导出泄漏一个 MemoryStream。
            http.Response.RegisterForDispose(export);

            return export.Format == ExportFormat.Csv
                ? Results.File(export.Content, "text/csv", "demo.csv")
                : Results.File(export.Content,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    "demo.xlsx");
        }).WithApiVersionSet(versions).HasApiVersion(new ApiVersion(1, 0));

        // CSV 导出演示
        app.MapGet("/demo/export-csv", async (
            HttpContext http,
            IDbContextFactory<DemoDbContext> f,
            ICsvExporter exporter,
            [AsParameters] PagedQuery query,
            CancellationToken ct) =>
        {
            using var ctx = await f.CreateDbContextAsync(ct);
            var q = ctx.Set<DemoEntity>().AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(query.Where))
                q = q.DynamicWhere(query.Where);
            q = q.DynamicOrderBy(query.OrderBy ?? "CreateTime desc");

            var export = await exporter.ExportAsync(q, new ExportOptions(), ct);
            http.Response.RegisterForDispose(export);
            return Results.File(export.Content, "text/csv", "demo.csv");
        }).WithApiVersionSet(versions).HasApiVersion(new ApiVersion(1, 0));

        // 导入：IFormFile → ImportExecutor（走 EntityService.CreateAsync 校验链）
        // 文件大小限制：防止超大文件（ClosedXML 全量加载到内存）被用于 DoS。
        app.MapPost("/demo/import", async (
            HttpContext http,
            IDbContextFactory<DemoDbContext> f,
            ImportExecutor executor,
            CancellationToken ct) =>
        {
            var form = await http.Request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(ApiResult<object>.Fail("未上传文件"));

            const long MaxImportBytes = 50 * 1024 * 1024; // 50 MB
            if (file.Length > MaxImportBytes)
                return Results.BadRequest(ApiResult<object>.Fail($"文件过大（{file.Length / 1024 / 1024} MB），上限 {MaxImportBytes / 1024 / 1024} MB"));

            var format = file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                ? ExportFormat.Csv
                : ExportFormat.Xlsx;

            await using var stream = file.OpenReadStream();
            var result = await executor.ExecuteAsync<DemoDbContext, DemoEntity>(
                f, stream, format, ct: ct);

            return ApiResultResult.Api(ApiResult<ImportResult>.Ok(result));
        }).WithApiVersionSet(versions).HasApiVersion(new ApiVersion(1, 0))
          .WithMetadata(new RequestSizeLimitAttribute(60 * 1024 * 1024));

        // 导入模板下载
        app.MapGet("/demo/import-template", async (
            HttpContext http,
            IImportTemplateGenerator generator,
            CancellationToken ct) =>
        {
            var ms = new MemoryStream();
            await generator.GenerateAsync<DemoEntity>(ms, ct);
            ms.Position = 0;
            http.Response.RegisterForDispose(ms);
            return Results.File(ms,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "demo-import-template.xlsx");
        }).WithApiVersionSet(versions).HasApiVersion(new ApiVersion(1, 0));

        // PostedBodyConvert 演示
        app.MapPost("/demo/posted-props", async (HttpContext http, CancellationToken ct) =>
        {
            var paths = await http.Request.GetPostedPropertiesAsync(ct);
            return Results.Ok(new { postedProperties = paths });
        }).WithApiVersionSet(versions).HasApiVersion(new ApiVersion(1, 0));

        // 部分更新演示：自动提取客户端提交的字段，传给 EntityService
        // #116: 用 host 配置的 JsonOptions（IOptions<JsonOptions>）而非裸 JsonSerializerOptions，
        // 与 TenE0ExceptionHandler 的序列化策略对齐 —— host 配置的 naming policy / number handling
        // 在此端点生效，避免 camelCase body 反序列化字段对不上。
        app.MapPut("/demo/partial/{id}", async (string id, HttpContext http, IDbContextFactory<DemoDbContext> f, IEntityService entitySvc, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions, CancellationToken ct) =>
        {
            var postedProps = await http.Request.GetPostedPropertiesAsync(ct);
            // GetPostedPropertiesAsync 已重置 Body 位置，可直接反序列化
            var entity = await System.Text.Json.JsonSerializer.DeserializeAsync<DemoEntity>(
                http.Request.Body,
                jsonOptions.Value.SerializerOptions,
                ct);

            if (entity is null) return Results.BadRequest("Invalid body");
            entity.Id = id;

            var options = new EntityWriteOptions
            {
                // 将 JSON 属性名（camelCase）转换为 C# 属性名（PascalCase）
                PostedProperties = new HashSet<string>(
                    postedProps.Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1))),
                FieldPermissions = DemoFieldPermissions.Map,
            };
            await using var dc = await f.CreateDbContextAsync(ct);
            var ok = await entitySvc.UpdateAsync(dc, entity, options, ct);
            return ok ? Results.Ok(new { ok = true, updatedFields = postedProps }) : Results.BadRequest("Update failed");
        }).WithApiVersionSet(versions).HasApiVersion(new ApiVersion(1, 0));

        return app;
    }
}
