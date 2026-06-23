using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TenE0.Api.Domain;
using TenE0.Api.Handlers;
using TenE0.Core.Abstractions;
using TenE0.Core.Common;
using TenE0.Core.EntityService;
using TenE0.Core.Errors;
using TenE0.Core.Json;
using TenE0.Core.Queries;

namespace TenE0.Api.Endpoints;

internal static class DemoEndpoints
{
    public static WebApplication MapDemoEndpoints(this WebApplication app)
    {
        app.MapGet("/whoami", (ICurrentUserContext user) => new
        {
            user = user.UserCode,
            authenticated = user.IsAuthenticated,
            roles = user.RoleIds,
        });

        // #39: 不再内联 try/catch PermissionDeniedException。
        // 所有 domain 异常由 TenE0ExceptionHandler 集中映射为 ApiResult<T> 形状。
        app.MapPost("/demo", async (CreateDemoDto dto, ICommandDispatcher dispatcher, IErrs errs, CancellationToken ct) =>
        {
            var id = await dispatcher.SendAsync(new CreateDemoCommand(dto.Name, dto.OrgId, dto.Salary), ct);
            return errs.IsValid
                ? ApiResultResult.Api(ApiResult<object>.Ok(new { id }))
                : ApiResultResult.Api(ApiResult<object>.FromErrs(errs));
        });

        app.MapPut("/demo/{id}", async (string id, UpdateDemoDto dto, ICommandDispatcher dispatcher, IErrs errs, CancellationToken ct) =>
        {
            var ok = await dispatcher.SendAsync(new UpdateDemoCommand(id, dto.Name, dto.Salary), ct);
            return ok && errs.IsValid
                ? ApiResultResult.Api(ApiResult<object>.Ok(new { ok = true }))
                : ApiResultResult.Api(ApiResult<object>.FromErrs(errs));
        });

        app.MapDelete("/demo/{id}", async (string id, ICommandDispatcher dispatcher, CancellationToken ct) =>
        {
            var ok = await dispatcher.SendAsync(new DeleteDemoCommand(id), ct);
            return ApiResultResult.Api(ApiResult<object>.Ok(new { ok }));
        });

        app.MapGet("/demo", async (ICommandDispatcher dispatcher, CancellationToken ct) =>
        {
            var list = await dispatcher.SendAsync(new ListDemosQuery(), ct);
            return ApiResultResult.Api(ApiResult<object>.Ok(list));
        });

        app.MapPost("/demo/{id}/publish", async (string id, ICommandDispatcher dispatcher, IErrs errs, CancellationToken ct) =>
        {
            var ok = await dispatcher.SendAsync(new PublishDemoCommand(id), ct);
            return ok && errs.IsValid
                ? ApiResultResult.Api(ApiResult<object>.Ok(new { ok = true }))
                : ApiResultResult.Api(ApiResult<object>.FromErrs(errs));
        });

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
        });

        // PostedBodyConvert 演示
        app.MapPost("/demo/posted-props", async (HttpContext http, CancellationToken ct) =>
        {
            var paths = await http.Request.GetPostedPropertiesAsync(ct);
            return Results.Ok(new { postedProperties = paths });
        });

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
        });

        return app;
    }
}
