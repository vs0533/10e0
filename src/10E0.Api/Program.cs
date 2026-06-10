using System.Linq.Expressions;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using TenE0.Core.Abstractions;
using TenE0.Core.Auth;
using TenE0.Core.DataContext;
using TenE0.Core.DependencyInjection;
using TenE0.Core.DynamicFilters;
using TenE0.Core.DynamicFilters.Storage;
using TenE0.Core.Entities;
using TenE0.Core.EntityService;
using TenE0.Core.EntityService.Validators;
using TenE0.Core.Hosting;
using TenE0.Core.Json;
using TenE0.Core.Queries;
using TenE0.Core.Permissions;
using TenE0.Core.Permissions.Behaviors;
using TenE0.Core.Permissions.DataFilter;
using TenE0.Core.Permissions.Management;
using TenE0.Core.Permissions.Storage;
using TenE0.Core.Auth.Jwt;
using TenE0.Core.Auth.Jwt.Commands;
using TenE0.Core.Auth.Jwt.Services;
using TenE0.Core.Auth.Jwt.Storage;
using TenE0.Core.Events;
using TenE0.Core.Events.Outbox;
using TenE0.Core.Menus;
using TenE0.Core.Menus.Storage;
using TenE0.Core.Organizations;
using TenE0.Core.Sequences;
using TenE0.Core.Sequences.Storage;
using TenE0.Core.Files;

var builder = WebApplication.CreateBuilder(args);

// -------- 服务注册 --------

builder.Services.AddTenE0Core();
builder.Services.AddTenE0EntityService();
builder.Services.AddTenE0DataContext<DemoDbContext>((_, options) =>
    options.UseInMemoryDatabase("10E0-demo-perm"));
builder.Services.AddTenE0Cqrs(typeof(Program).Assembly);
builder.Services.AddTenE0PermissionsFromAssembly(typeof(Program).Assembly);
builder.Services.AddTenE0Menus<DemoDbContext>();

// Identity 模式：一行注册 JWT + 权限 + 组织树（含扩展用户字段：AppUser）
builder.Services.AddTenE0Identity<AppUser, DemoDbContext>(opt =>
{
    opt.Jwt.Issuer = "10E0.Api";
    opt.Jwt.Audience = "10E0.Api";
    opt.Jwt.SigningKey = builder.Configuration["Jwt:SigningKey"]
        ?? "dev-secret-CHANGE-ME-in-production-must-be-at-least-32-bytes-long";
    opt.Jwt.AccessTokenLifetime = TimeSpan.FromMinutes(30);
    opt.Jwt.RefreshTokenLifetime = TimeSpan.FromDays(14);
    opt.Permissions.SuperUserRoles.Add("super_admin");
});

// 流水号生成器 + 领域事件
builder.Services.AddTenE0Sequences<DemoDbContext>();
builder.Services.AddTenE0DomainEvents<DemoDbContext>(opt =>
{
    opt.BatchSize = 50;
    opt.PollInterval = TimeSpan.FromMilliseconds(500);
});
builder.Services.AddTenE0DomainEventHandlersFromAssembly(typeof(Program).Assembly);

// 动态数据过滤
builder.Services.AddTenE0DynamicFilters<DemoDbContext>();

// 文件上传
builder.Services.AddTenE0Files<DemoDbContext>(options =>
{
    options.BasePath = "uploads";
    options.BaseUrl = "/uploads";
});

// Seeder：初始权限授予 + 管理员账号 + 组织树
builder.Services.AddScoped<IDataSeeder, PermissionSeeder>();
builder.Services.AddScoped<IDataSeeder, AuthSeeder>();
builder.Services.AddScoped<IDataSeeder, MenuSeeder>();

builder.Services.AddScoped<IUserInfoLoader, NullUserInfoLoader>();
builder.Services.AddOpenApi();

builder.Services.AddAuthorization();
builder.Services.AddControllers();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// 静态文件服务（用于本地存储）
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.MapGet("/", () => new { name = "10E0.Api", status = "ok" });

// ----------------- 认证端点 -----------------

app.MapPost("/auth/login", async (LoginCommand cmd, ICommandDispatcher d, IErrs errs, HttpContext http, CancellationToken ct) =>
{
    var withIp = cmd with { ClientIp = http.Connection.RemoteIpAddress?.ToString() };
    var result = await d.SendAsync(withIp, ct);
    return errs.IsValid
        ? Results.Ok(result)
        : Results.Json(new { error = errs.GetFirstError() }, statusCode: 401);
});

app.MapPost("/auth/refresh", async (RefreshTokenCommand cmd, ICommandDispatcher d, IErrs errs, HttpContext http, CancellationToken ct) =>
{
    var withIp = cmd with { ClientIp = http.Connection.RemoteIpAddress?.ToString() };
    var result = await d.SendAsync(withIp, ct);
    return errs.IsValid
        ? Results.Ok(result)
        : Results.Json(new { error = errs.GetFirstError() }, statusCode: 401);
});

app.MapPost("/auth/logout", async (LogoutCommand cmd, ICommandDispatcher d, CancellationToken ct) =>
{
    await d.SendAsync(cmd, ct);
    return Results.Ok(new { ok = true });
});

// ----------------- 业务端点 -----------------

app.MapGet("/whoami", (ICurrentUserContext user) => new
{
    user = user.UserCode,
    authenticated = user.IsAuthenticated,
    roles = user.RoleIds,
});

app.MapPost("/demo", async (CreateDemoDto dto, ICommandDispatcher dispatcher, IErrs errs, CancellationToken ct) =>
{
    try
    {
        var id = await dispatcher.SendAsync(new CreateDemoCommand(dto.Name, dto.OrgId, dto.Salary), ct);
        return errs.IsValid
            ? Results.Ok(new { id })
            : Results.BadRequest(new { error = errs.GetFirstError(), keys = errs.Keys });
    }
    catch (PermissionDeniedException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
});

app.MapPut("/demo/{id}", async (string id, UpdateDemoDto dto, ICommandDispatcher dispatcher, IErrs errs, CancellationToken ct) =>
{
    try
    {
        var ok = await dispatcher.SendAsync(new UpdateDemoCommand(id, dto.Name, dto.Salary), ct);
        return ok && errs.IsValid
            ? Results.Ok(new { ok = true })
            : Results.BadRequest(new { error = errs.GetFirstError(), keys = errs.Keys });
    }
    catch (PermissionDeniedException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
});

app.MapDelete("/demo/{id}", async (string id, ICommandDispatcher dispatcher, CancellationToken ct) =>
{
    try
    {
        var ok = await dispatcher.SendAsync(new DeleteDemoCommand(id), ct);
        return Results.Ok(new { ok });
    }
    catch (PermissionDeniedException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
});

app.MapGet("/demo", async (ICommandDispatcher dispatcher, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await dispatcher.SendAsync(new ListDemosQuery(), ct));
    }
    catch (PermissionDeniedException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
});

app.MapPost("/demo/{id}/publish", async (string id, ICommandDispatcher dispatcher, IErrs errs, CancellationToken ct) =>
{
    try
    {
        var ok = await dispatcher.SendAsync(new PublishDemoCommand(id), ct);
        return ok && errs.IsValid
            ? Results.Ok(new { ok = true })
            : Results.BadRequest(new { error = errs.GetFirstError() });
    }
    catch (PermissionDeniedException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
});

// ----------------- 动态查询演示 -----------------

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

// ----------------- PostedBodyConvert 演示 -----------------

app.MapPost("/demo/posted-props", async (HttpContext http, CancellationToken ct) =>
{
    var paths = await http.Request.GetPostedPropertiesAsync(ct);
    return Results.Ok(new { postedProperties = paths });
});

// 部分更新演示：自动提取客户端提交的字段，传给 EntityService
app.MapPut("/demo/partial/{id}", async (string id, HttpContext http, IDbContextFactory<DemoDbContext> f, IEntityService entitySvc, CancellationToken ct) =>
{
    var postedProps = await http.Request.GetPostedPropertiesAsync(ct);
    // GetPostedPropertiesAsync 已重置 Body 位置，可直接反序列化
    var entity = await System.Text.Json.JsonSerializer.DeserializeAsync<DemoEntity>(
        http.Request.Body,
        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true },
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

// 加载动态过滤规则
{
    using var scope = app.Services.CreateScope();
    var filterProvider = scope.ServiceProvider.GetRequiredService<IDynamicFilterProvider>();
    // 从 DbContext 获取连接信息来加载规则
    var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DemoDbContext>>();
    using var ctx = await contextFactory.CreateDbContextAsync();
    var providerName = ctx.Database.ProviderName ?? "";
    // InMemory 数据库不支持关系型方法，跳过动态过滤规则加载
    if (providerName.Contains("InMemory"))
    {
        Console.WriteLine("[DynamicFilters] Skipping rule load: InMemory database");
    }
    else
    {
        var connStr = ctx.Database.GetConnectionString() ?? "";
        // 将 EF provider name 映射为 ADO.NET provider invariant name
        // Microsoft.EntityFrameworkCore.SqlServer → Microsoft.Data.SqlClient
        // (System.Data.SqlClient 已 archive，.NET 10 / EF Core 10 默认使用 Microsoft.Data.SqlClient)
        if (!string.IsNullOrEmpty(connStr))
        {
            var adoProvider = providerName switch
            {
                "Microsoft.EntityFrameworkCore.SqlServer" => "Microsoft.Data.SqlClient",
                "Npgsql.EntityFrameworkCore.PostgreSQL" => "Npgsql",
                "Pomelo.EntityFrameworkCore.MySql" => "MySqlConnector",
                _ => providerName
            };
            await filterProvider.LoadRulesAsync(connStr, adoProvider);
        }
    } // end else (non-InMemory)
}

// ----------------- 文件上传 API -----------------

app.MapPost("/files/upload", async (IFormFile file, IFileService fileSvc, IErrs errs, CancellationToken ct) =>
{
    if (file == null || file.Length == 0)
    {
        errs.Add("文件不能为空", "file", "FILE_EMPTY");
        return Results.BadRequest(new { error = "文件不能为空" });
    }

    using var stream = file.OpenReadStream();
    var response = await fileSvc.UploadAsync(stream, file.FileName, file.ContentType, ct: ct);

    return Results.Ok(response);
})
.WithName("UploadFile")
.WithDescription("上传文件");

app.MapPost("/files/upload/image", async (IFormFile file, IFileService fileSvc, IErrs errs,
    int? width, int? height, bool generateThumbnail, int quality, string? watermarkText, CancellationToken ct) =>
{
    if (file == null || file.Length == 0)
    {
        errs.Add("文件不能为空", "file", "FILE_EMPTY");
        return Results.BadRequest(new { error = "文件不能为空" });
    }

    if (!file.ContentType.StartsWith("image/"))
    {
        errs.Add("只能上传图片文件", "file", "NOT_IMAGE");
        return Results.BadRequest(new { error = "只能上传图片文件" });
    }

    var options = new ImageProcessOptions
    {
        Width = width,
        Height = height,
        GenerateThumbnail = generateThumbnail,
        Quality = quality > 0 ? quality : 85,
        WatermarkText = watermarkText
    };

    using var stream = file.OpenReadStream();
    var response = await fileSvc.UploadImageAsync(stream, file.FileName, options, ct: ct);

    return Results.Ok(response);
})
.WithName("UploadImage")
.WithDescription("上传图片（支持处理选项）");

app.MapGet("/files/{id}", async (string id, IFileService fileSvc, CancellationToken ct) =>
{
    var (stream, metadata) = await fileSvc.DownloadAsync(id, ct);
    if (stream == null || metadata == null)
    {
        return Results.NotFound(new { error = "文件不存在" });
    }

    return Results.File(stream, metadata.ContentType, metadata.FileName);
})
.WithName("DownloadFile")
.WithDescription("下载文件");

app.MapDelete("/files/{id}", async (string id, IFileService fileSvc, CancellationToken ct) =>
{
    var deleted = await fileSvc.DeleteAsync(id, ct);
    if (!deleted)
    {
        return Results.NotFound(new { error = "文件不存在或已删除" });
    }

    return Results.Ok(new { message = "删除成功" });
})
.WithName("DeleteFile")
.WithDescription("删除文件");

app.MapGet("/files/{id}/metadata", async (string id, IFileService fileSvc, CancellationToken ct) =>
{
    var metadata = await fileSvc.GetMetadataAsync(id, ct);
    if (metadata == null)
    {
        return Results.NotFound(new { error = "文件不存在" });
    }

    var accessUrl = await fileSvc.GetAccessUrlAsync(id, ct);

    return Results.Ok(new FileResponse(
        metadata.Id,
        metadata.FileName,
        metadata.ContentType,
        metadata.FileSize,
        accessUrl!,
        metadata.ThumbnailPath != null ? $"{accessUrl}/thumb" : null,
        metadata.Width,
        metadata.Height,
        metadata.Category,
        metadata.CreateTime
    ));
})
.WithName("GetFileMetadata")
.WithDescription("获取文件元数据");

app.Run();

// 自定义认证简写（要求 super_admin 或具备 perm.admin 权限）
internal sealed class RequireAdminAttribute : Microsoft.AspNetCore.Authorization.AuthorizeAttribute
{
    public RequireAdminAttribute() { /* 简化：Dev 模式仅认证即可，权限交由 PermissionBehavior 验，此处用于演示 */ }
}

// ============================================================
// 权限定义
// ============================================================

internal static class DemoPermissions
{
    public const string View = "demo.view";
    public const string Create = "demo.create";
    public const string Update = "demo.update";
    public const string Delete = "demo.delete";
    public const string ManageSalary = "demo.field.salary";  // 字段级权限
    public const string Admin = "perm.admin";                 // 后台管理权限
}

internal sealed class DemoPermissionProvider : IPermissionProvider
{
    public IEnumerable<PermissionDefinition> Define() =>
    [
        new(DemoPermissions.View,         "查看 Demo",          "demo"),
        new(DemoPermissions.Create,       "创建 Demo",          "demo"),
        new(DemoPermissions.Update,       "更新 Demo",          "demo"),
        new(DemoPermissions.Delete,       "删除 Demo",          "demo"),
        new(DemoPermissions.ManageSalary, "维护 Demo 薪资字段", "demo"),
        new(DemoPermissions.Admin,        "权限后台",           "system"),
    ];
}

// ============================================================
// 启动时初始化角色 + 默认 grants
// ============================================================

internal sealed class PermissionSeeder(IDbContextFactory<DemoDbContext> dcFactory) : IDataSeeder
{
    public int Order => 100;

    public async Task SeedAsync(DbContext context, CancellationToken cancellationToken)
    {
        await using var dc = await dcFactory.CreateDbContextAsync(cancellationToken);

        if (await dc.Roles.AnyAsync(cancellationToken)) return; // 幂等

        dc.Roles.AddRange(
            new TenE0Role { Code = "viewer", Name = "查看者" },
            new TenE0Role { Code = "editor", Name = "编辑者" },
            new TenE0Role { Code = "manager", Name = "管理者" },
            new TenE0Role { Code = "hr", Name = "人事（管薪资）" },
            new TenE0Role { Code = "super_admin", Name = "超级管理员" });

        dc.RolePermissions.AddRange(
            // viewer
            new() { RoleCode = "viewer", PermissionKey = DemoPermissions.View },
            // editor
            new() { RoleCode = "editor", PermissionKey = DemoPermissions.View },
            new() { RoleCode = "editor", PermissionKey = DemoPermissions.Create },
            new() { RoleCode = "editor", PermissionKey = DemoPermissions.Update },
            // manager（含删除，但不含薪资字段）
            new() { RoleCode = "manager", PermissionKey = DemoPermissions.View },
            new() { RoleCode = "manager", PermissionKey = DemoPermissions.Create },
            new() { RoleCode = "manager", PermissionKey = DemoPermissions.Update },
            new() { RoleCode = "manager", PermissionKey = DemoPermissions.Delete },
            new() { RoleCode = "manager", PermissionKey = DemoPermissions.Admin },
            // hr（专门管薪资字段；含 Create 用于演示创建带 Salary 的实体）
            new() { RoleCode = "hr", PermissionKey = DemoPermissions.View },
            new() { RoleCode = "hr", PermissionKey = DemoPermissions.Create },
            new() { RoleCode = "hr", PermissionKey = DemoPermissions.Update },
            new() { RoleCode = "hr", PermissionKey = DemoPermissions.ManageSalary });

        await dc.SaveChangesAsync(cancellationToken);
    }
}

// ============================================================
// 数据行过滤器：按 Org 隔离
// ============================================================

internal sealed class DemoOrgScopedFilter : EntityFilterContributor<DemoEntity>
{
    protected override Expression<Func<DemoEntity, bool>>? Build(BaseDataContext context)
    {
        var dc = (DemoDbContext)context;
        return entity =>
            dc.BypassFilters
            || !dc.IsAuthenticated
            || entity.OrgId == null
            || entity.OrgId == dc.CurrentOrgId;
    }
}

// ============================================================
// 实体定义
// ============================================================

/// <summary>
/// 业务 DbContext — 继承框架的 TenE0SystemDbContext 后，框架全部表自动接入。
/// 这里只声明业务自己的 Demos 表 + 取 CurrentOrgId 的便捷属性。
/// </summary>
internal sealed class DemoDbContext(
    DbContextOptions<DemoDbContext> options,
    ICurrentUserContext currentUser,
    IDataAccessPolicy accessPolicy,
    IHttpContextAccessor httpContextAccessor,
    IEnumerable<IEntityFilterContributor> filters,
    IDynamicFilterProvider dynamicFilterProvider)
    : TenE0SystemDbContext<AppUser, TenE0Role>(options, currentUser, accessPolicy, filters, dynamicFilterProvider)
{
    public string? CurrentOrgId { get; } =
        httpContextAccessor.HttpContext?.User?.FindFirstValue("org");

    public DbSet<DemoEntity> Demos => Set<DemoEntity>();
}

/// <summary>
/// 业务方扩展的用户实体 — 演示 Identity 模式的核心能力。
/// 继承 TenE0User 后，框架的登录/刷新/JWT 流程自动用 AppUser 类型查询。
/// EF Core TPH：AppUser 和未来其他子类自动同表存储。
/// </summary>
internal sealed class AppUser : TenE0User
{
    public string? Avatar { get; set; }
    public string? Department { get; set; }
    public DateOnly? Birthday { get; set; }
}

// DemoEntity 升级为聚合根：业务方法触发事件，OutboxInterceptor 自动持久化事件
internal sealed class DemoEntity : AggregateRoot
{
    // 流水号自动生成：每天重置，4 位补零，前缀 "DEMO-"
    [Sequence("demo", "DEMO-{yyyyMMdd}-{0000}")]
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";
    public string? OrgId { get; set; }
    public decimal? Salary { get; set; }

    /// <summary>
    /// 标记"已发布"。仅业务方法可以触发状态变化，并附带事件。
    /// 这是 DDD 的典型用法：状态变更通过聚合方法暴露，外界用 method 而不是直接 set。
    /// </summary>
    public bool IsPublished { get; private set; }

    public void Publish(string publisherCode)
    {
        if (IsPublished)
            throw new InvalidOperationException($"Demo {Id} 已发布，不可重复发布");

        IsPublished = true;
        Raise(new DemoPublishedEvent(Id, Code, Name, publisherCode, OrgId));
    }
}

// ============================================================
// 领域事件
// ============================================================

internal sealed record DemoCreatedEvent(string Id, string Code, string Name, string? OrgId) : IDomainEvent;

internal sealed record DemoPublishedEvent(string Id, string Code, string Name, string PublishedBy, string? OrgId) : IDomainEvent;

// ============================================================
// DTOs
// ============================================================

internal sealed record CreateDemoDto(string Name, string? OrgId, decimal? Salary);
internal sealed record UpdateDemoDto(string Name, decimal? Salary);
internal sealed record CreateOrgDto(string Code, string Name, string? ParentId, string? Description, int Order);
internal sealed record MoveOrgDto(string? NewParentId);

// ============================================================
// 命令
// ============================================================

[RequirePermission(DemoPermissions.View)]
internal sealed record ListDemosQuery : IQuery<List<DemoView>>;

[RequirePermission(DemoPermissions.Create)]
internal sealed record CreateDemoCommand(string Name, string? OrgId, decimal? Salary) : ICommand<string>;

[RequirePermission(DemoPermissions.Update)]
internal sealed record UpdateDemoCommand(string Id, string Name, decimal? Salary) : ICommand<bool>;

[RequirePermission(DemoPermissions.Delete)]
internal sealed record DeleteDemoCommand(string Id) : ICommand<bool>;

[RequirePermission(DemoPermissions.Update)]
internal sealed record PublishDemoCommand(string Id) : ICommand<bool>;

// ============================================================
// Handlers
// ============================================================

internal sealed record DemoView(string Id, string Code, string Name, string? OrgId, decimal? Salary, DateTimeOffset? CreateTime);

// 字段权限映射（共享）
internal static class DemoFieldPermissions
{
    public static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>
    {
        [nameof(DemoEntity.Salary)] = DemoPermissions.ManageSalary,
    };
}

internal sealed class CreateDemoCommandHandler(IDbContextFactory<DemoDbContext> dcFactory, IEntityService entitySvc)
    : ICommandHandler<CreateDemoCommand, string>
{
    public async Task<string> HandleAsync(CreateDemoCommand command, CancellationToken ct)
    {
        await using var dc = await dcFactory.CreateDbContextAsync(ct);
        var demo = new DemoEntity { Name = command.Name, OrgId = command.OrgId, Salary = command.Salary };

        // BeforeSaveAsync 在 SaveChanges 之前、流水号已分配之后触发 — 此时 demo.Code 才是有效的
        await entitySvc.CreateAsync(dc, demo, new EntityWriteOptions
        {
            UniqueValidators = [Unique.Field(demo, x => x.Name)],
            FieldPermissions = command.Salary.HasValue ? DemoFieldPermissions.Map : null,
            BeforeSaveAsync = _ =>
            {
                // 直接调聚合的 Raise 方法（protected） — 这里我们用反射或者把 Raise 改 public？
                // 更优雅的做法是聚合自己提供 OnCreated 方法。这里用 helper 触发事件。
                DemoEventTrigger.RaiseCreated(demo);
                return Task.CompletedTask;
            }
        }, ct);
        return demo.Id;
    }
}

// 聚合根的 Raise 是 protected，外部无法直接调用 — 这是 DDD 的封装。
// 但 EntityService 创建场景下我们需要触发事件，可在聚合内提供 internal 方法（避免暴露 Raise）。
// 这里为简洁起见用反射 trick，生产中推荐在 DemoEntity 中暴露 OnCreated 方法。
internal static class DemoEventTrigger
{
    private static readonly System.Reflection.MethodInfo RaiseMethod =
        typeof(AggregateRoot).GetMethod("Raise",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    public static void RaiseCreated(DemoEntity demo) =>
        RaiseMethod.Invoke(demo, [new DemoCreatedEvent(demo.Id, demo.Code, demo.Name, demo.OrgId)]);
}

internal sealed class PublishDemoCommandHandler(IDbContextFactory<DemoDbContext> dcFactory, ICurrentUserContext currentUser, IErrs errs)
    : ICommandHandler<PublishDemoCommand, bool>
{
    public async Task<bool> HandleAsync(PublishDemoCommand command, CancellationToken ct)
    {
        await using var dc = await dcFactory.CreateDbContextAsync(ct);
        var demo = await dc.Demos.FirstOrDefaultAsync(d => d.Id == command.Id, ct);
        if (demo is null)
        {
            errs.Add("Demo 不存在", code: "NOT_FOUND");
            return false;
        }

        try
        {
            demo.Publish(currentUser.UserCode ?? "anonymous");   // 触发 DemoPublishedEvent
        }
        catch (InvalidOperationException ex)
        {
            errs.Add(ex.Message, code: "INVALID_STATE");
            return false;
        }

        await dc.SaveChangesAsync(ct);   // 业务状态 + OutboxMessage 同事务原子提交
        return true;
    }
}

// ============================================================
// 领域事件订阅者
// ============================================================

internal sealed class DemoCreatedAuditHandler(ILogger<DemoCreatedAuditHandler> logger)
    : IDomainEventHandler<DemoCreatedEvent>
{
    public Task HandleAsync(DemoCreatedEvent evt, CancellationToken ct)
    {
        logger.LogInformation("[EVENT] DemoCreated 收到：Id={Id} Code={Code} Name={Name} Org={Org}",
            evt.Id, evt.Code, evt.Name, evt.OrgId);
        return Task.CompletedTask;
    }
}

internal sealed class DemoPublishedNotificationHandler(ILogger<DemoPublishedNotificationHandler> logger)
    : IDomainEventHandler<DemoPublishedEvent>
{
    public Task HandleAsync(DemoPublishedEvent evt, CancellationToken ct)
    {
        logger.LogInformation("[EVENT] DemoPublished 收到（发通知/索引/推送等）：Id={Id} Code={Code} 由 {By} 发布",
            evt.Id, evt.Code, evt.PublishedBy);
        return Task.CompletedTask;
    }
}

internal sealed class DemoPublishedAuditHandler(ILogger<DemoPublishedAuditHandler> logger)
    : IDomainEventHandler<DemoPublishedEvent>
{
    public Task HandleAsync(DemoPublishedEvent evt, CancellationToken ct)
    {
        logger.LogInformation("[EVENT] DemoPublished 收到（写审计日志）：Id={Id} By={By} At=now",
            evt.Id, evt.PublishedBy);
        return Task.CompletedTask;
    }
}

internal sealed class UpdateDemoCommandHandler(IDbContextFactory<DemoDbContext> dcFactory, IEntityService entitySvc)
    : ICommandHandler<UpdateDemoCommand, bool>
{
    public async Task<bool> HandleAsync(UpdateDemoCommand command, CancellationToken ct)
    {
        await using var dc = await dcFactory.CreateDbContextAsync(ct);
        var demo = new DemoEntity { Id = command.Id, Name = command.Name, Salary = command.Salary };

        // 客户端实际提交了哪些字段：演示场景下 Name 总改；Salary 仅在提供值时改
        var posted = new HashSet<string> { nameof(DemoEntity.Name) };
        if (command.Salary.HasValue) posted.Add(nameof(DemoEntity.Salary));

        return await entitySvc.UpdateAsync(dc, demo, new EntityWriteOptions
        {
            PostedProperties = posted,
            UniqueValidators = [Unique.Field(demo, x => x.Name)],
            FieldPermissions = DemoFieldPermissions.Map,
        }, ct);
    }
}

internal sealed class DeleteDemoCommandHandler(IDbContextFactory<DemoDbContext> dcFactory, IEntityService entitySvc)
    : ICommandHandler<DeleteDemoCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteDemoCommand command, CancellationToken ct)
    {
        await using var dc = await dcFactory.CreateDbContextAsync(ct);
        return await entitySvc.DeleteAsync(dc, new DemoEntity { Id = command.Id }, ct);
    }
}

internal sealed class ListDemosQueryHandler(IDbContextFactory<DemoDbContext> dcFactory)
    : ICommandHandler<ListDemosQuery, List<DemoView>>
{
    public async Task<List<DemoView>> HandleAsync(ListDemosQuery query, CancellationToken ct)
    {
        await using var dc = await dcFactory.CreateDbContextAsync(ct);
        return await dc.Demos
            .Select(d => new DemoView(d.Id, d.Code, d.Name, d.OrgId, d.Salary, d.CreateTime))
            .ToListAsync(ct);
    }
}

// ============================================================
// 初始数据：管理员账号 + 一棵示例组织树
// ============================================================

internal sealed class AuthSeeder(
    IDbContextFactory<DemoDbContext> dcFactory,
    IPasswordHasher passwordHasher,
    IOrgTreeService orgTree) : IDataSeeder
{
    public int Order => 200;   // 在 PermissionSeeder(100) 之后跑，保证角色已存在

    public async Task SeedAsync(DbContext _, CancellationToken ct)
    {
        await using var dc = await dcFactory.CreateDbContextAsync(ct);

        if (!await dc.Users.AnyAsync(ct))
        {
            // 默认管理员：admin / 111111 — 用扩展的 AppUser 类型，演示新增字段也能直接用
            dc.Users.Add(new AppUser
            {
                UserCode = "admin",
                DisplayName = "系统管理员",
                PasswordHash = passwordHasher.Hash("111111"),
                IsActive = true,
                UserType = UserType.Person,
                Avatar = "/avatars/admin.png",
                Department = "信息中心",
            });

            // 普通用户：alice / 111111
            dc.Users.Add(new AppUser
            {
                UserCode = "alice",
                DisplayName = "Alice",
                PasswordHash = passwordHasher.Hash("111111"),
                IsActive = true,
                Avatar = "/avatars/alice.png",
                Department = "市场部",
                Birthday = new DateOnly(1995, 6, 15),
            });

            // 角色绑定
            dc.UserRoles.AddRange(
                new TenE0UserRole { UserCode = "admin", RoleCode = "super_admin" },
                new TenE0UserRole { UserCode = "admin", RoleCode = "manager" },
                new TenE0UserRole { UserCode = "alice", RoleCode = "editor" });

            await dc.SaveChangesAsync(ct);
        }

        if (!await dc.Orgs.AnyAsync(ct))
        {
            // 组织树：集团 → 北京/上海 → 销售/技术
            var hq = await orgTree.AddAsync("HQ", "集团总部", cancellationToken: ct);
            var bj = await orgTree.AddAsync("BJ", "北京分公司", parentId: hq.Id, cancellationToken: ct);
            var sh = await orgTree.AddAsync("SH", "上海分公司", parentId: hq.Id, cancellationToken: ct);
            await orgTree.AddAsync("BJ-SALES", "北京销售部", parentId: bj.Id, cancellationToken: ct);
            await orgTree.AddAsync("BJ-TECH", "北京技术部", parentId: bj.Id, cancellationToken: ct);
            await orgTree.AddAsync("SH-SALES", "上海销售部", parentId: sh.Id, cancellationToken: ct);
        }
    }
}

internal sealed class NullUserInfoLoader : IUserInfoLoader
{
    public ValueTask<ICurrentUserInfo?> LoadAsync(string userCode, UserType userType, CancellationToken cancellationToken)
        => ValueTask.FromResult<ICurrentUserInfo?>(null);
    public string Serialize(ICurrentUserInfo info) => string.Empty;
    public ICurrentUserInfo? Deserialize(string payload, UserType userType) => null;
}
