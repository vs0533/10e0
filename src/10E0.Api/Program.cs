using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using TenE0.Api.Domain;
using TenE0.Api.Endpoints;
using TenE0.Api.Hosting;
using TenE0.Api.Seeders;
using TenE0.Core.DependencyInjection;
using TenE0.Core.Errors;
using TenE0.Core.Hosting;

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
// Jwt:SigningKey 必须从配置/环境变量/密钥管理服务读取，未配置时启动期
// JwtOptionsValidator + ValidateOnStart 会抛 OptionsValidationException 拒绝启动。
builder.Services.AddTenE0Identity<AppUser, DemoDbContext>(opt =>
{
    opt.Jwt.Issuer = "10E0.Api";
    opt.Jwt.Audience = "10E0.Api";
    opt.Jwt.SigningKey = builder.Configuration["Jwt:SigningKey"]
        ?? throw new InvalidOperationException(
            "Jwt:SigningKey 未配置。请通过 appsettings.json / 环境变量 JWT__SigningKey / " +
            "dotnet user-secrets / 密钥管理服务注入。dev 模式可在 appsettings.Development.json 设置。");
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

// #39: 集中异常映射 (PermissionDenied → 403, Validation → 400, DbUpdate → 409, 其余 → 500)
builder.Services.AddTenE0ExceptionHandler();

// Seeder：初始权限授予 + 管理员账号 + 组织树
builder.Services.AddScoped<IDataSeeder, PermissionSeeder>();
builder.Services.AddScoped<IDataSeeder, AuthSeeder>();
builder.Services.AddScoped<IDataSeeder, MenuSeeder>();

// IUserInfoLoader 默认实现由 AddTenE0Core() 通过 TryAddScoped 注册（#43 下沉），
// 这里不再重复 AddScoped —— 否则会在 Api 端解析成 Api.Hosting.NullUserInfoLoader
// 而非 Core 版本（issue #93 修复后 Api 自带副本已删）。
builder.Services.AddOpenApi();

// #119: 注册 perm.admin Authorization Policy。底层走 IPermissionEvaluator，
// 与 PermissionBehavior 共用 super_admin bypass + role-version 检查 — 保证
// Minimal API 端点（如 /admin/outbox）和 CQRS 命令走同一套权限评估。
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        TenE0.Core.Permissions.PermissionPolicies.Admin,
        policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new TenE0.Core.Permissions.PermissionRequirement(
                TenE0.Api.Domain.DemoPermissions.Admin)));
});
builder.Services.AddScoped<
    Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
    TenE0.Core.Permissions.PermissionAuthorizationHandler>();
builder.Services.AddControllers();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// #39: 集中异常处理 — 放在 pipeline 最前面，最早接住未处理异常。
// 传入空 configure 委托是为了满足 ExceptionHandlerMiddleware 的"必须设置
// ExceptionHandler / ExceptionHandlingPath / IExceptionHandlerOptions"约束
// (仅传 IExceptionHandler 服务不够，middleware 还会做 null-check 抛
// InvalidOperationException)。实际异常分发由注册到 DI 的 TenE0ExceptionHandler
// 负责，configure 委托本身不做任何事情。
app.UseExceptionHandler(_ => { });

// 静态文件服务（用于本地存储）
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// 加载动态过滤规则（启动时一次性）
await DynamicFilterBootstrap.LoadRulesAsync(app);

// 路由映射
app.MapHealthEndpoints()
   .MapAuthEndpoints()
   .MapDemoEndpoints()
   .MapAdminEndpoints()
   .MapFileEndpoints();

app.Run();
