using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using TenE0.Api.Domain;
using TenE0.Api.Endpoints;
using TenE0.Api.Hosting;
using TenE0.Api.Modules;
using TenE0.Core.Certificate.Pdf;
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

    // #161 可观测性：注册 TenE0Metrics + HealthChecks（DbContext/Outbox/FileStorage）。
    // OTel SDK 追踪/导出 + Prometheus /metrics 端点在下方 app 层装配。
    opt.Observability = true;
    opt.ObservabilityOptions = obs =>
    {
        obs.ServiceName = "10E0.Api";
        obs.OtlpEndpoint = builder.Configuration["OTEL:Endpoint"];
    };

    // #164 定时任务调度：注册 SchedulerWorker + IScheduler + 静态任务扫描。
    // demo 用短扫描间隔（10s）便于观察；生产建议默认 30s。
    opt.Scheduling = true;
    opt.SchedulingOptions = sched =>
    {
        sched.ScanInterval = TimeSpan.FromSeconds(10);
    };

    // #185 证书生成：注册 ICertificateService + 占位渲染器。
    // 依赖 Files（证书 PDF 存 IFileService）—— 上面 opt.Files = true 已开。
    // 证书编号走 Sequence（opt.Sequences 默认开），key 用默认 "certificate"。
    opt.Certificate = true;
    opt.CertificateOptions = cert =>
    {
        cert.SequenceKey = "certificate";
        cert.SequenceFormat = "CERT-{yyyyMMdd}-{0000}";
    };
});

// #161 可观测性 —— app 层装配 OTel SDK（core 不带 OTel 依赖，避免框架包膨胀）。
// metrics 常开（含自定义 Meter("TenE0") + AspNetCore/Http/EFCore instrument + Prometheus exporter）；
// tracing 仅当配置了 OTEL:Endpoint 时接 OTLP（开发环境默认不导出，避免无 Collector 时刷错日志）。
{
    // OTel SDK 装配读 Observability 段（含上面 options lambda 写入的 ServiceName / OtlpEndpoint）。
    var otelOpts = builder.Configuration.GetSection("Observability")
        .Get<TenE0.Core.Observability.ObservabilityOptions>() ?? new();

    var otel = builder.Services.AddOpenTelemetry();
    otel.ConfigureResource(r => r.AddService(otelOpts.ServiceName));

    // Metrics：自定义 Meter + 框架内置 instrument + Prometheus 导出。
    otel.WithMetrics(m => m
        .AddMeter(TenE0.Core.Observability.TenE0Metrics.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddPrometheusExporter());

    // Tracing：仅配置了 OTLP 端点时启用（OTLP exporter 需要 Collector，开发默认无）。
    var otlp = otelOpts.OtlpEndpoint;
    if (!string.IsNullOrWhiteSpace(otlp))
    {
        otel.WithTracing(t => t
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddEntityFrameworkCoreInstrumentation()
            .AddSource(TenE0.Core.Observability.TenE0Metrics.MeterName)
            .AddOtlpExporter(o => o.Endpoint = new Uri(otlp)));
    }
}

// 业务部分（demo 专属）：seeder / 系统参数定义 / AssigneeDirectory / InMemory 装配器。
builder.Services.AddAppModule<DemoAppModule>(builder.Configuration);

// #185 证书 PDF 渲染器：Replace 占位渲染器为 PDFsharp 实现（独立包 10E0.Core.Certificate）。
// 主包 TenE0.Core 零 PDF 依赖；本 demo ProjectRef 了独立包，故此处 Replace 生效。
// 业务项目若用自定义渲染器，可改 Replace 为自己的 ICertificateRenderer 实现。
builder.Services.AddTenE0PdfCertificateRenderer();

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
   .MapWorkflowEndpoints()
   .MapCertificateEndpoints();

// #161 标准健康端点：/health/live（匿名恒 200）、/health/ready（匿名就绪）。
// /health 完整报告需 perm.admin（含每项 check 详情/积压数，敏感）。
app.MapTenE0HealthChecks(adminAuthorizationPolicy: TenE0.Core.Permissions.PermissionPolicies.Admin);

// Prometheus 抓取端点：需 perm.admin（service account token 抓取），避免内部指标外泄。
app.MapPrometheusScrapingEndpoint("/metrics")
   .RequireAuthorization(TenE0.Core.Permissions.PermissionPolicies.Admin);

// 实时推送 Hub 端点（/hub/notification）—— 必须在 UseAuthentication/UseAuthorization 之后。
app.MapTenE0Hub();

app.Run();
