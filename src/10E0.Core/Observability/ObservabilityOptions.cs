namespace TenE0.Core.Observability;

/// <summary>
/// 可观测性模块配置（#161）。
///
/// <para>
/// 覆盖三块能力：<b>健康检查</b>（HealthChecks）、<b>指标</b>（Metrics，基于
/// <c>System.Diagnostics.Metrics</c>）、以及给应用层的 <b>OTel 追踪/导出</b>提供
/// 服务名等元信息。Core 自身<b>零新依赖</b> —— HealthChecks 与 Metrics API 都在
/// <c>Microsoft.AspNetCore.App</c> 共享框架内；OpenTelemetry SDK / OTLP / Prometheus
/// 导出器由应用层按需引用（见 <c>docs/26-observability.md</c>），避免框架 NuGet 包膨胀。
/// </para>
/// </summary>
public sealed class ObservabilityOptions
{
    /// <summary>
    /// 配置节名。应用可通过 <c>services.Configure&lt;ObservabilityOptions&gt;(configuration.GetSection(SectionName))</c>
    /// 从 <c>appsettings.json</c> 的 <c>"Observability"</c> 段绑定。
    /// </summary>
    public const string SectionName = "Observability";

    /// <summary>
    /// 服务名 —— 写入 OTel Resource / Prometheus 指标标签 / 健康报告，便于多服务区分。
    /// </summary>
    public string ServiceName { get; set; } = "TenE0";

    /// <summary>
    /// OTLP 导出端点（如 <c>http://otel-collector:4317</c>）。
    /// 为 <c>null</c> 时不导出追踪（仅 Core 内该值仅作透传；真正接 OTLP 由应用层 OTel SDK 完成）。
    /// </summary>
    public string? OtlpEndpoint { get; set; }

    /// <summary>
    /// 是否启用 ASP.NET Core HealthChecks 注册与端点。默认 <c>true</c>。
    /// </summary>
    public bool EnableHealthChecks { get; set; } = true;

    /// <summary>
    /// 单个健康检查的超时时间。避免某个依赖 hang 住拖垮 K8s readiness 探针。默认 3 秒。
    /// </summary>
    public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Outbox 积压达到此值时健康检查报告 <c>Degraded</c>（降级）。
    /// 积压数 = <c>OutboxMessage</c> 中 <c>SentTime == null &amp;&amp; AttemptCount &lt; MaxAttempts</c> 的行数。
    /// 默认 100。
    /// </summary>
    public int OutboxDegradedThreshold { get; set; } = 100;

    /// <summary>
    /// Outbox 积压达到此值时健康检查报告 <c>Unhealthy</c>（不可用）。默认 1000。
    /// </summary>
    public int OutboxUnhealthyThreshold { get; set; } = 1000;
}
