using Asp.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

// 本模块自有的 ApiVersioningOptions 与 Asp.Versioning.ApiVersioningOptions 同名，
// 用别名消歧；对外公开签名仍用 TenE0ApiVersioningOptions（业务方配置的是框架行为，而非 Asp.Versioning 内部选项）。
using TenE0ApiVersioningOptions = TenE0.Core.ApiVersioning.ApiVersioningOptions;

namespace TenE0.Core.DependencyInjection;

/// <summary>
/// API 版本化模块的 DI / 端点注册扩展（#163）。
/// </summary>
/// <remarks>
/// 基于 <see cref="Asp.Versioning"/>（社区标准，原 Microsoft.AspNetCore.Mvc.Versioning 迁移而来），
/// 在 .NET 10 内置 OpenAPI 基础上提供多版本共存与每版本独立文档。
/// </remarks>
public static class ApiVersioningExtensions
{
    /// <summary>
    /// 注册 API 版本化基础设施：版本读取器（URL segment + query + header 三者并存）+
    /// API Explorer（驱动 OpenAPI 多版本文档）+ 版本感知 OpenAPI 文档生成。
    ///
    /// 默认策略为「版本透明」：<see cref="ApiVersioningOptions.AssumeDefaultVersionWhenUnspecified"/> = <c>true</c>，
    /// 未声明版本的请求按默认版本（1.0）处理，保证既有裸路由端点引入版本化后行为零变化（向后兼容）。
    ///
    /// 三种版本声明方式并存（任选其一）：
    /// <list type="bullet">
    /// <item>URL segment：<c>/v1/demo</c>（最直观，易缓存）</item>
    /// <item>Query string：<c>?api-version=1.0</c></item>
    /// <item>Header：<c>X-Api-Version: 1.0</c></item>
    /// </list>
    ///
    /// OpenAPI 文档按 <c>GroupNameFormat = "v'VVV"</c> 分组（v1、v2.1…），每版本一份独立文档，
    /// 配合 <see cref="MapTenE0OpenApi"/> 在 Scalar UI 中按版本切换。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">可选的配置覆盖委托。</param>
    public static IServiceCollection AddTenE0ApiVersioning(
        this IServiceCollection services,
        Action<TenE0ApiVersioningOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<TenE0ApiVersioningOptions>();

        services.AddApiVersioning()
            .AddApiExplorer(opt =>
            {
                // GroupNameFormat: 'v'VVV → v1, v2.1（用于 OpenAPI 文档分组名）
                opt.GroupNameFormat = "'v'VVV";
                // URL segment 模式下，把 {version:apiVersion} 替换成实际版本号，OpenAPI 文档路径更直观
                opt.SubstituteApiVersionInUrl = true;
            })
            .AddOpenApi();

        // Asp.Versioning 的 ApiVersioningOptions 走 IOptions<T> 解析。用带 sp 的 Configure<IServiceProvider>
        // 桥接框架配置（延迟到 options 首次解析时执行，sp 安全，不触发 root provider 过早构建）。
        services.AddOptions<Asp.Versioning.ApiVersioningOptions>().Configure<IServiceProvider>((aspOpt, sp) =>
        {
            var o = sp.GetRequiredService<IOptions<TenE0ApiVersioningOptions>>().Value;
            aspOpt.DefaultApiVersion = new ApiVersion(o.DefaultMajorVersion, o.DefaultMinorVersion);
            aspOpt.AssumeDefaultVersionWhenUnspecified = o.AssumeDefaultVersionWhenUnspecified;
            aspOpt.ReportApiVersions = o.ReportApiVersions;
            aspOpt.ApiVersionReader = ApiVersionReader.Combine(
                new UrlSegmentApiVersionReader(),
                new QueryStringApiVersionReader(),
                new HeaderApiVersionReader("X-Api-Version"));
        });

        return services;
    }

    /// <summary>
    /// 注册版本化的 OpenAPI 端点，按 API 版本自动生成每版本一份文档。
    ///
    /// 与原生 <c>app.MapOpenApi()</c> 的区别：<see cref="Asp.Versioning.Builder.IEndpointConventionBuilderExtensions.WithDocumentPerVersion"/>
    /// 让单个 OpenAPI 端点依据 <c>GroupNameFormat</c> 产出 <c>/openapi/v1.json</c>、<c>/openapi/v2.json</c> 等多份文档，
    /// Scalar UI 据此提供版本下拉切换。
    ///
    /// 应仅在 Development 环境调用（与既有「OpenAPI 仅 Dev」约定一致）：
    /// <code>if (app.Environment.IsDevelopment()) app.MapTenE0OpenApi();</code>
    /// </summary>
    /// <param name="endpoints">来自 <see cref="WebApplication"/>（同时实现 <see cref="IEndpointRouteBuilder"/>）。</param>
    public static IEndpointConventionBuilder MapTenE0OpenApi(this IEndpointRouteBuilder endpoints)
        => endpoints.MapOpenApi().WithDocumentPerVersion();
}
