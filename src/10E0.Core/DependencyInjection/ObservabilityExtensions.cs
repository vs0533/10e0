using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using System.Text.Json;
using TenE0.Core.Files;
using TenE0.Core.Observability;
using TenE0.Core.Observability.HealthChecks;

namespace TenE0.Core.DependencyInjection;

/// <summary>
/// 可观测性模块的 DI / 端点注册扩展（#161）。
///
/// <para>
/// 一键装配：<b>Metrics</b>（<see cref="TenE0Metrics"/>，DI Singleton）+
/// <b>HealthChecks</b>（DbContext / Outbox / 可选 FileStorage）。
/// <b>零新依赖</b> —— 二者均来自 <c>Microsoft.AspNetCore.App</c> 共享框架。
/// OTel 追踪 / OTLP / Prometheus 导出器由应用层按需引用并装配（见 <c>docs/26-observability.md</c>）。
/// </para>
/// </summary>
public static class ObservabilityExtensions
{
    /// <summary>健康检查标签：标记 "就绪" 探针纳入的检查（供 <c>/health/ready</c> 过滤）。</summary>
    public const string ReadyTag = "ready";

    /// <summary>
    /// 注册可观测性基础设施。
    ///
    /// <para>
    /// Metrics：注册 <see cref="TenE0Metrics"/> 为 Singleton —— 即使无读取者也可安全埋点
    /// （<c>CommandDispatcher</c> / <c>OutboxRelayService</c> 通过 <c>GetService</c> 解析，未注册时 no-op）。
    /// </para>
    /// <para>
    /// HealthChecks：始终挂 DbContext + Outbox；仅当 <see cref="IFileStorage"/> 已注册
    /// （Files 模块启用）时挂 FileStorage。全部带 <see cref="ReadyTag"/>，由 <see cref="MapTenE0HealthChecks"/>
    /// 的 <c>/health/ready</c> 纳入。
    /// </para>
    /// </summary>
    /// <typeparam name="TContext">应用 DbContext 类型。</typeparam>
    /// <param name="configure">选项回调；不传用默认值。</param>
    public static IServiceCollection AddTenE0Observability<TContext>(
        this IServiceCollection services,
        Action<ObservabilityOptions>? configure = null)
        where TContext : DbContext
    {
        // 实例化默认 options 并应用 callback 一次。这份已配置的实例同时用于：
        // a) 注册到 IOptions<ObservabilityOptions>（业务方/测试可解析）；
        // b) 决定是否启用 HealthChecks；
        // c) per-check 超时（喂给 AddCheck）。
        // 不用 BuildServiceProvider 读 IOptions —— 那会触发单例重复；也不二次调用 callback
        // （Options.Create 把同一份实例包成 IOptions<T> 单例，零额外开销）。
        var options = new ObservabilityOptions();
        configure?.Invoke(options);
        services.AddSingleton<IOptions<ObservabilityOptions>>(Options.Create(options));

        // Metrics：TryAdd 让测试 / 业务方可 Replace；未启用时埋点 no-op。
        services.TryAddSingleton<TenE0Metrics>();

        if (options.EnableHealthChecks)
        {
            // AddCheck 的 timeout 重载：DefaultHealthCheckService 会用它为每个 check
            // 套带超时的 CancellationToken —— 避免单个依赖 hang 住拖垮 K8s readiness 探针。
            var healthChecks = services.AddHealthChecks();
            var timeout = options.HealthCheckTimeout;
            healthChecks
                .AddCheck<DbContextHealthCheck<TContext>>("db", failureStatus: null, tags: [ReadyTag], timeout)
                .AddCheck<OutboxHealthCheck<TContext>>("outbox", failureStatus: null, tags: [ReadyTag], timeout);

            // 仅当 Files 模块启用（IFileStorage 已注册）才挂文件存储检查 ——
            // 避免对未启用文件功能的项目引入无意义的 Unhealthy。
            var filesEnabled = services.Any(d => d.ServiceType == typeof(IFileStorage));
            if (filesEnabled)
                healthChecks.AddCheck<FileStorageHealthCheck>("files", failureStatus: null, tags: [ReadyTag], timeout);
        }

        return services;
    }

    /// <summary>
    /// 注册标准健康端点：
    /// <list type="bullet">
    /// <item><c>GET /health/live</c> —— 进程存活，匿名。不跑任何检查，只要进程在就 200（K8s liveness）。</item>
    /// <item><c>GET /health/ready</c> —— 就绪，匿名。跑带 <see cref="ReadyTag"/> 的检查（DB/Outbox/Files），全 Healthy 才 200（K8s readiness）。</item>
    /// <item><c>GET /health</c> —— 完整 JSON 报告（含每项 check 状态/耗时/数据）。需鉴权（见 <paramref name="adminAuthorizationPolicy"/>）。</item>
    /// </list>
    /// </summary>
    /// <param name="endpoints">来自 <see cref="WebApplication"/>。</param>
    /// <param name="adminAuthorizationPolicy">
    /// <c>/health</c> 完整报告所需的授权策略名（如 <c>PermissionPolicies.Admin</c>）。
    /// 为 <c>null</c> 时不挂鉴权（仅适合内网/开发；生产应传入策略名，避免依赖详情与积压数外泄）。
    /// </param>
    public static IEndpointRouteBuilder MapTenE0HealthChecks(
        this IEndpointRouteBuilder endpoints,
        string? adminAuthorizationPolicy = null)
    {
        // per-check 超时在 AddTenE0Observability 注册时已通过 HealthCheckRegistration.Timeout 应用；
        // 这里只负责端点映射。

        // live：恒 200（predicate 全 false = 不跑任何 check）。
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteMinimalStatusResponse,
            ResultStatusCodes = StatusCodesByHealth,
        });

        // ready：只跑带 "ready" 标签的检查。
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = reg => reg.Tags.Contains(ReadyTag),
            ResultStatusCodes = StatusCodesByHealth,
        });

        // 完整报告：跑所有检查，输出详细 JSON。需鉴权（含每项耗时/数据，敏感）。
        var full = endpoints.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = WriteHealthReportJson,
            ResultStatusCodes = StatusCodesByHealth,
        });
        if (adminAuthorizationPolicy is not null)
            full.RequireAuthorization(adminAuthorizationPolicy);

        return endpoints;

        // —— 本地函数 ——

        // /health/live 的最小输出（不暴露详情）。
        static Task WriteMinimalStatusResponse(HttpContext context, HealthReport report)
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            return context.Response.WriteAsync("""{"status":"ok"}""");
        }

        // /health 的详细 JSON 报告。
        static Task WriteHealthReportJson(HttpContext context, HealthReport report)
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            var entries = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString().ToLowerInvariant(),
                durationMs = e.Value.Duration.TotalMilliseconds,
                description = e.Value.Description,
                error = e.Value.Exception?.Message,
                data = e.Value.Data,
            });
            var payload = new
            {
                status = report.Status.ToString().ToLowerInvariant(),
                totalDurationMs = report.TotalDuration.TotalMilliseconds,
                entries,
            };
            return context.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonOptions));
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // K8s 探针友好的状态码（默认实现已是这套，显式写出避免歧义）。
    // ready 允许 Degraded 仍 200（摘流由 Unhealthy 决定）。
    private static readonly Dictionary<HealthStatus, int> StatusCodesByHealth = new()
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
    };
}
