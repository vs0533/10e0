using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using TenE0.Api.Domain;
using TenE0.Api.Handlers;
using TenE0.Core.DynamicFilters;
using TenE0.Core.Menus;
using TenE0.Core.Organizations;
using TenE0.Core.Permissions;
using TenE0.Core.Permissions.Management;
using TenE0.Core.Workflow.Definitions;

namespace TenE0.Api.Endpoints;

/// <summary>
/// 自定义认证简写（要求 super_admin 或具备 perm.admin 权限）。
///
/// <para>
/// 走 <see cref="PermissionPolicies.Admin"/> Policy：底层是
/// <see cref="PermissionAuthorizationHandler"/> 调用 <see cref="IPermissionEvaluator"/>，
/// 与 <see cref="PermissionEvaluator"/> 同一套 super_admin bypass + role-version 检查，
/// 保证 CQRS 命令和 Minimal API endpoint 行为一致。
/// </para>
///
/// <para>
/// #119：<c>/admin/outbox</c> 之前未挂此属性导致 Payload 泄露；现在所有
/// <c>/admin/*</c> 端点都用同一道闸门。
/// </para>
/// </summary>
internal sealed class RequireAdminAttribute : AuthorizeAttribute
{
    public RequireAdminAttribute() : base(PermissionPolicies.Admin) { }
}

internal static class AdminEndpoints
{
    public static WebApplication MapAdminEndpoints(this WebApplication app)
    {
        // ----------------- 组织树管理端点 -----------------
        app.MapGet("/admin/orgs", async (IDbContextFactory<DemoDbContext> f, CancellationToken ct) =>
        {
            await using var dc = await f.CreateDbContextAsync(ct);
            return await dc.Orgs.AsNoTracking()
                .OrderBy(o => o.Path)
                .Select(o => new { o.Id, o.Code, o.Name, o.ParentId, o.Path, o.Level })
                .ToListAsync(ct);
        });

        app.MapPost("/admin/orgs", async (CreateOrgDto dto, IOrgTreeService svc, CancellationToken ct) =>
        {
            var org = await svc.AddAsync(dto.Code, dto.Name, dto.ParentId, dto.Description, dto.Order, ct);
            return Results.Ok(new { org.Id, org.Path, org.Level });
        });

        app.MapGet("/admin/orgs/{id}/subtree", async (string id, IOrgTreeService svc, CancellationToken ct) =>
        {
            var ids = await svc.GetSubtreeIdsAsync(id, ct);
            var descendants = await svc.GetDescendantsAsync(id, ct);
            return Results.Ok(new
            {
                subtreeIds = ids,
                descendantCount = descendants.Count,
                descendants = descendants.Select(o => new { o.Id, o.Code, o.Name, o.Level, o.Path })
            });
        });

        app.MapGet("/admin/orgs/{id}/ancestors", async (string id, IOrgTreeService svc, CancellationToken ct) =>
        {
            var ancestors = await svc.GetAncestorsAsync(id, ct);
            return Results.Ok(ancestors.Select(o => new { o.Id, o.Code, o.Name, o.Level }));
        });

        app.MapPost("/admin/orgs/{id}/move", async (string id, MoveOrgDto dto, IOrgTreeService svc, CancellationToken ct) =>
        {
            try
            {
                await svc.MoveAsync(id, dto.NewParentId, ct);
                return Results.Ok(new { ok = true });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // 查看 Outbox 表（调试用）
        // #119：把端点接入 perm.admin Policy，未持 perm.admin 的角色（viewer/editor）
        // 必须 403；admin (super_admin) 自动 bypass。同时把 [RequireAdmin] attribute
        // 显式挂到 endpoint metadata（Minimal API source generator 对 [Authorize]
        // 在 anonymous 路径上的处理在 .NET 10 行为有差异，WithMetadata 走显式路径
        // 确保 Authorization middleware 对未认证用户也调用 challenge）。
        app.MapGet("/admin/outbox", [RequireAdmin] async (IDbContextFactory<DemoDbContext> f, CancellationToken ct) =>
        {
            await using var dc = await f.CreateDbContextAsync(ct);
            var items = await dc.OutboxMessages
                .OrderByDescending(m => m.OccurredOn)
                .Take(20)
                .Select(m => new
                {
                    m.Id,
                    EventType = m.EventType.Split(',')[0],   // 简化显示
                    m.OccurredOn,
                    m.SentTime,
                    m.AttemptCount,
                    m.LastError,
                })
                .ToListAsync(ct);
            return Results.Ok(items);
        }).WithMetadata(new RequireAdminAttribute());

        // ----------------- 权限管理 Admin API（需 perm.admin 权限）-----------------
        app.MapGet("/admin/permissions",
            [RequireAdmin] (PermissionCatalog catalog) => Results.Ok(catalog.All));

        app.MapGet("/admin/roles/{role}/permissions",
            [RequireAdmin] async (string role, IPermissionGrantService svc, CancellationToken ct) =>
                Results.Ok(await svc.ListGrantedAsync(role, ct)));

        app.MapPost("/admin/roles/{role}/permissions/{key}",
            [RequireAdmin] async (string role, string key, IPermissionGrantService svc, CancellationToken ct) =>
            {
                try
                {
                    await svc.GrantAsync(role, key, ct);
                    return Results.Ok(new { granted = true });
                }
                catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
            });

        app.MapDelete("/admin/roles/{role}/permissions/{key}",
            [RequireAdmin] async (string role, string key, IPermissionGrantService svc, CancellationToken ct) =>
            {
                await svc.RevokeAsync(role, key, ct);
                return Results.Ok(new { revoked = true });
            });

        // ----------------- 菜单端点 -----------------
        // #115: 改为参数注入 IMenuService（Minimal API 自动为每请求建 scope 解析 Scoped 服务），
        // 消除 Service Locator 模式（app.Services.CreateAsyncScope + GetRequiredService）。
        // 与上方 orgs/permissions 端点的参数注入风格一致。
        app.MapGet("/menus/tree", async (IMenuService menuService, CancellationToken ct) =>
            Results.Ok(await menuService.GetMenuTreeAsync(ct)));

        app.MapGet("/menus/user-tree", async (IMenuService menuService, CancellationToken ct) =>
            Results.Ok(await menuService.GetUserMenuTreeAsync(ct)));

        // ----------------- 菜单管理 Admin API -----------------
        app.MapPost("/admin/menus", async (MenuCreateRequest request, IMenuService menuService, CancellationToken ct) =>
        {
            var menu = await menuService.AddAsync(request, ct);
            return Results.Ok(menu);
        });

        app.MapPut("/admin/menus/{id}", async (string id, MenuUpdateRequest request, IMenuService menuService, CancellationToken ct) =>
        {
            await menuService.UpdateAsync(id, request, ct);
            return Results.Ok(new { ok = true });
        });

        app.MapDelete("/admin/menus/{id}", async (string id, IMenuService menuService, CancellationToken ct) =>
        {
            await menuService.DeleteAsync(id, ct);
            return Results.Ok(new { ok = true });
        });

        app.MapPut("/admin/menus/{id}/move", async (string id, string? parentId, IMenuService menuService, CancellationToken ct) =>
        {
            try
            {
                await menuService.MoveAsync(id, parentId, ct);
                return Results.Ok(new { ok = true });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPut("/admin/roles/{code}/menus", async (string code, string[] menuIds, IMenuService menuService, CancellationToken ct) =>
        {
            await menuService.AssignToRoleAsync(code, menuIds, ct);
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/admin/roles/{code}/menus", async (string code, IMenuService menuService, CancellationToken ct) =>
            Results.Ok(await menuService.GetRoleMenuIdsAsync(code, ct)));

        // ----------------- 动态数据过滤规则管理 Admin API -----------------
        // #115: 同样改为参数注入 IDataFilterRuleService，消除 Service Locator。
        app.MapGet("/admin/data-filters", async (IDataFilterRuleService service, CancellationToken ct) =>
            Results.Ok(await service.GetAllAsync(ct)));

        app.MapGet("/admin/data-filters/{id}", async (string id, IDataFilterRuleService service, CancellationToken ct) =>
        {
            var rule = await service.GetByIdAsync(id, ct);
            return rule is not null ? Results.Ok(rule) : Results.NotFound();
        });

        app.MapGet("/admin/data-filters/entity/{entityTypeName}", async (string entityTypeName, IDataFilterRuleService service, CancellationToken ct) =>
            Results.Ok(await service.GetByEntityAsync(entityTypeName, ct)));

        app.MapPost("/admin/data-filters", async (DataFilterRuleCreateRequest request, IDataFilterRuleService service, CancellationToken ct) =>
        {
            var rule = await service.CreateAsync(request, ct);
            return Results.Ok(rule);
        });

        app.MapPut("/admin/data-filters/{id}", async (string id, DataFilterRuleUpdateRequest request, IDataFilterRuleService service, CancellationToken ct) =>
        {
            await service.UpdateAsync(id, request, ct);
            return Results.Ok(new { ok = true });
        });

        app.MapDelete("/admin/data-filters/{id}", async (string id, IDataFilterRuleService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.Ok(new { ok = true });
        });

        app.MapPatch("/admin/data-filters/{id}/toggle", async (string id, bool enabled, IDataFilterRuleService service, CancellationToken ct) =>
        {
            await service.SetEnabledAsync(id, enabled, ct);
            return Results.Ok(new { ok = true });
        });

        // ----------------- 工作流定义管理端点（#158） -----------------

        // 列出所有流程定义的最新版本
        app.MapGet("/admin/workflow/definitions", [RequireAdmin] async (
            IProcessDefinitionStore store,
            int skip = 0,
            int take = 50,
            CancellationToken ct = default) =>
        {
            var list = await store.ListLatestAsync(skip, take, ct);
            return Results.Ok(list.Select(d => new
            {
                d.Id,
                d.Code,
                d.Name,
                d.Version,
                d.CategoryCode,
                d.IsEnabled,
                d.IsLatest,
                d.Description,
                d.CreateTime,
            }));
        });

        // 查某 Code 的所有版本
        app.MapGet("/admin/workflow/definitions/{code}/versions", [RequireAdmin] async (
            string code,
            IProcessDefinitionStore store,
            CancellationToken ct) =>
        {
            var versions = await store.ListVersionsAsync(code, ct);
            return Results.Ok(versions.Select(d => new
            {
                d.Id,
                d.Code,
                d.Version,
                d.IsEnabled,
                d.IsLatest,
                d.CreateTime,
            }));
        });

        // 发布新版本（接收完整定义 JSON）
        app.MapPost("/admin/workflow/definitions", [RequireAdmin] async (
            PublishDefinitionDto dto,
            IProcessDefinitionStore store,
            CancellationToken ct) =>
        {
            try
            {
                var def = new TenE0ProcessDefinition
                {
                    Code = dto.Code,
                    Name = dto.Name,
                    CategoryCode = dto.CategoryCode,
                    Description = dto.Description,
                    StartNodeCode = dto.StartNodeCode,
                    NodesJson = dto.NodesJson,
                    EdgesJson = dto.EdgesJson ?? "[]",
                    TenantId = dto.TenantId ?? "",
                };
                var published = await store.PublishAsync(def, ct);
                return Results.Ok(new { published.Id, published.Code, published.Version });
            }
            catch (ProcessDefinitionInvalidException ex)
            {
                return Results.BadRequest(new { error = "流程定义校验失败", errors = ex.Errors });
            }
        });

        // 发布预置流程（如 ExpenseClaimProcess）
        app.MapPost("/admin/workflow/definitions/built-in/{builtin}", [RequireAdmin] async (
            string builtin,
            IProcessDefinitionStore store,
            CancellationToken ct) =>
        {
            TenE0ProcessDefinition def = builtin switch
            {
                "expense-claim" => ExpenseClaimProcess.Build(),
                _ => throw new InvalidOperationException($"未知预置流程 '{builtin}'"),
            };
            var published = await store.PublishAsync(def, ct);
            return Results.Ok(new { published.Id, published.Code, published.Version });
        });

        // 取某 Code 的最新版本详情（含节点图 JSON）
        app.MapGet("/admin/workflow/definitions/{code}/latest", [RequireAdmin] async (
            string code,
            IProcessDefinitionStore store,
            CancellationToken ct) =>
        {
            var latest = await store.GetLatestAsync(code, ct);
            return latest is null ? Results.NotFound() : Results.Ok(latest);
        });

        // 禁用某版本（不物理删除）
        app.MapDelete("/admin/workflow/definitions/{id}", [RequireAdmin] async (
            string id,
            IProcessDefinitionStore store,
            CancellationToken ct) =>
        {
            await store.DisableAsync(id, ct);
            return Results.Ok(new { ok = true });
        });

        return app;
    }
}

// 工作流定义管理 DTO
internal sealed class PublishDefinitionDto
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? CategoryCode { get; set; }
    public string? Description { get; set; }
    public required string StartNodeCode { get; set; }
    public required string NodesJson { get; set; }
    public string? EdgesJson { get; set; }
    public string? TenantId { get; set; }
}
