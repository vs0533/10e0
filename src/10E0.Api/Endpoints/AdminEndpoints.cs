using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using TenE0.Api.Domain;
using TenE0.Api.Handlers;
using TenE0.Core.DynamicFilters;
using TenE0.Core.Menus;
using TenE0.Core.Organizations;
using TenE0.Core.Permissions;
using TenE0.Core.Permissions.Management;

namespace TenE0.Api.Endpoints;

/// <summary>
/// 自定义认证简写（要求 super_admin 或具备 perm.admin 权限）。
/// 简化：Dev 模式仅认证即可，权限交由 PermissionBehavior 验，此处用于演示。
/// </summary>
internal sealed class RequireAdminAttribute : AuthorizeAttribute
{
    public RequireAdminAttribute() { }
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
        app.MapGet("/admin/outbox", async (IDbContextFactory<DemoDbContext> f, CancellationToken ct) =>
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
        });

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
        app.MapGet("/menus/tree", async (CancellationToken ct) =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            var menuService = scope.ServiceProvider.GetRequiredService<IMenuService>();
            return Results.Ok(await menuService.GetMenuTreeAsync(ct));
        });

        app.MapGet("/menus/user-tree", async (CancellationToken ct) =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            var menuService = scope.ServiceProvider.GetRequiredService<IMenuService>();
            return Results.Ok(await menuService.GetUserMenuTreeAsync(ct));
        });

        // ----------------- 菜单管理 Admin API -----------------
        app.MapPost("/admin/menus", async (MenuCreateRequest request, CancellationToken ct) =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            var menuService = scope.ServiceProvider.GetRequiredService<IMenuService>();
            var menu = await menuService.AddAsync(request, ct);
            return Results.Ok(menu);
        });

        app.MapPut("/admin/menus/{id}", async (string id, MenuUpdateRequest request, CancellationToken ct) =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            var menuService = scope.ServiceProvider.GetRequiredService<IMenuService>();
            await menuService.UpdateAsync(id, request, ct);
            return Results.Ok(new { ok = true });
        });

        app.MapDelete("/admin/menus/{id}", async (string id, CancellationToken ct) =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            var menuService = scope.ServiceProvider.GetRequiredService<IMenuService>();
            await menuService.DeleteAsync(id, ct);
            return Results.Ok(new { ok = true });
        });

        app.MapPut("/admin/menus/{id}/move", async (string id, string? parentId, CancellationToken ct) =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            var menuService = scope.ServiceProvider.GetRequiredService<IMenuService>();
            try
            {
                await menuService.MoveAsync(id, parentId, ct);
                return Results.Ok(new { ok = true });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPut("/admin/roles/{code}/menus", async (string code, string[] menuIds, CancellationToken ct) =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            var menuService = scope.ServiceProvider.GetRequiredService<IMenuService>();
            await menuService.AssignToRoleAsync(code, menuIds, ct);
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/admin/roles/{code}/menus", async (string code, CancellationToken ct) =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            var menuService = scope.ServiceProvider.GetRequiredService<IMenuService>();
            return Results.Ok(await menuService.GetRoleMenuIdsAsync(code, ct));
        });

        // ----------------- 动态数据过滤规则管理 Admin API -----------------
        app.MapGet("/admin/data-filters", async (CancellationToken ct) =>
        {
            using var scope = app.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IDataFilterRuleService>();
            return Results.Ok(await service.GetAllAsync(ct));
        });

        app.MapGet("/admin/data-filters/{id}", async (string id, CancellationToken ct) =>
        {
            using var scope = app.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IDataFilterRuleService>();
            var rule = await service.GetByIdAsync(id, ct);
            return rule is not null ? Results.Ok(rule) : Results.NotFound();
        });

        app.MapGet("/admin/data-filters/entity/{entityTypeName}", async (string entityTypeName, CancellationToken ct) =>
        {
            using var scope = app.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IDataFilterRuleService>();
            return Results.Ok(await service.GetByEntityAsync(entityTypeName, ct));
        });

        app.MapPost("/admin/data-filters", async (DataFilterRuleCreateRequest request, CancellationToken ct) =>
        {
            using var scope = app.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IDataFilterRuleService>();
            var rule = await service.CreateAsync(request, ct);
            return Results.Ok(rule);
        });

        app.MapPut("/admin/data-filters/{id}", async (string id, DataFilterRuleUpdateRequest request, CancellationToken ct) =>
        {
            using var scope = app.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IDataFilterRuleService>();
            await service.UpdateAsync(id, request, ct);
            return Results.Ok(new { ok = true });
        });

        app.MapDelete("/admin/data-filters/{id}", async (string id, CancellationToken ct) =>
        {
            using var scope = app.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IDataFilterRuleService>();
            await service.DeleteAsync(id, ct);
            return Results.Ok(new { ok = true });
        });

        app.MapPatch("/admin/data-filters/{id}/toggle", async (string id, bool enabled, CancellationToken ct) =>
        {
            using var scope = app.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IDataFilterRuleService>();
            await service.SetEnabledAsync(id, enabled, ct);
            return Results.Ok(new { ok = true });
        });

        return app;
    }
}
