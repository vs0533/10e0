using Scalar.AspNetCore;
using TenE0.Api.Domain;
using TenE0.Api.Endpoints;
using TenE0.Api.Hosting;
using TenE0.Api.Modules;
using TenE0.Core.DependencyInjection;
using TenE0.Core.Errors;
using TenE0.Core.Security.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// -------- 框架装配（issue #160：AddTenE0All 一键注册 + DemoAppModule 业务下沉） --------

// 框架部分：一行 AddTenE0All 替代原来的 15+ 行 AddTenE0Xxx 样板。
// 默认启用 Core/EntityService/DataContext/Cqrs/Permissions/Identity/Menus/Sequences/
// DomainEvents/DynamicFilters/Configuration；按需启用项在 options 里 opt-in（见下）。
builder.Services.AddTenE0All<AppUser, DemoDbContext>(builder.Configuration, opt =>
{
    // demo 用 InMemory（显式指定 provider，避免连接串探测）。生产改 SqlServer/PostgreSQL/SQLite
    // 并通过 DemoAppModule 注册对应 IDbProviderConfigurator。
    opt.Provider = DatabaseProvider.InMemory;
    opt.ConnectionString = "10E0-demo-perm"; // InMemory 用作数据库名
    opt.HandlerAssemblies = [typeof(Program).Assembly];

    // 必填：JWT SigningKey 从配置/环境变量/密钥管理服务读取。
    // JwtOptionsValidator + ValidateOnStart 在未配置或为占位符时拒绝启动。
    opt.Identity = identity =>
    {
        identity.Jwt.Issuer = "10E0.Api";
        identity.Jwt.Audience = "10E0.Api";
        identity.Jwt.SigningKey = builder.Configuration["Jwt:SigningKey"]
            ?? throw new InvalidOperationException(
                "Jwt:SigningKey 未配置。请通过 appsettings.json / 环境变量 JWT__SigningKey / " +
                "dotnet user-secrets / 密钥管理服务注入。dev 模式可在 appsettings.Development.json 设置。");
        identity.Jwt.AccessTokenLifetime = TimeSpan.FromMinutes(30);
        identity.Jwt.RefreshTokenLifetime = TimeSpan.FromDays(14);
        identity.Permissions.SuperUserRoles.Add("super_admin");
    };

    // 按需启用项（默认关）
    opt.Auditing = true;       // #152
    opt.Files = true;          // 文件上传（本地存储）
    opt.FilesOptions = files =>
    {
        files.BasePath = "uploads";
        files.BaseUrl = "/uploads";
    };
    opt.ImportExport = true;   // #154
    opt.Realtime = true;       // #155
    opt.Workflow = true;       // #156 epic
    opt.DomainEventsOptions = relay =>
    {
        relay.BatchSize = 50;
        relay.PollInterval = TimeSpan.FromMilliseconds(500);
    };

    // #162 安全防刷三件套：限流 + 登录失败锁定 + 验证码。
    // 限流 / 验证码端点 + pipeline 由下方 UseTenE0RateLimiting / MapCaptchaEndpoints 接入。
    opt.RateLimiting = true;
    opt.LoginProtection = true;
    opt.LoginProtectionOptions = lp =>
    {
        lp.MaxFailedAttempts = 5;
        lp.LockoutDuration = TimeSpan.FromMinutes(15);
    };
    opt.Captcha = true;
    opt.CaptchaOptions = cap =>
    {
        // demo 默认关验证码强制，避免每次登录都填；可改 Always / AfterFailures 验证效果。
        cap.LoginTrigger = TenE0.Core.Security.Captcha.CaptchaTrigger.Disabled;
    };
});

// 业务部分（demo 专属）：seeder / 系统参数定义 / AssigneeDirectory / InMemory 装配器。
builder.Services.AddAppModule<DemoAppModule>(builder.Configuration);

// #39: 集中异常映射 (PermissionDenied → 403, Validation → 400, DbUpdate → 409, 其余 → 500)
builder.Services.AddTenE0ExceptionHandler();

// API 版本化（#163）：版本透明策略（默认版本 1.0，未声明版本按默认处理，向后兼容裸路由），
// 同时注册版本感知 OpenAPI 文档生成（每版本一份，配合下方 MapTenE0OpenApi 在 Scalar 切换）。
builder.Services.AddTenE0ApiVersioning();

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
    // #163：MapTenE0OpenApi 包装 MapOpenApi().WithDocumentPerVersion()，
    // 按 GroupNameFormat 产出每版本一份 OpenAPI 文档，Scalar UI 据此切换版本。
    app.MapTenE0OpenApi();
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

// #162 限流：必须放在 UseRouting 之后、UseAuthentication 之后（user 分区可用），
// 在 UseAuthorization 之前（未授权请求也能按 IP 限流，防匿名刷）。
app.UseTenE0RateLimiting();

app.UseAuthorization();
app.MapControllers();

// 加载动态过滤规则（启动时一次性）
await DynamicFilterBootstrap.LoadRulesAsync(app);

// 业务模块路由（demo 各端点）—— 端点扩展签名在 WebApplication 上，
// 与 IAppModule.MapEndpoints(IEndpointRouteBuilder) 契约不兼容，故此处显式挂载。
app.MapHealthEndpoints()
   .MapAuthEndpoints()
   .MapCaptchaEndpoints()
   .MapDemoEndpoints()
   .MapAdminEndpoints()
   .MapFileEndpoints()
   .MapWorkflowEndpoints();

// 实时推送 Hub 端点（/hub/notification）—— 必须在 UseAuthentication/UseAuthorization 之后。
app.MapTenE0Hub();

app.Run();
