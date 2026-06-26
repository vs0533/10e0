using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TenE0.Core.Events.Outbox;

namespace TenE0.Core.Observability.HealthChecks;

/// <summary>
/// Outbox 积压健康检查（#161）。
///
/// <para>
/// 积压数 = <c>OutboxMessage</c> 中 <c>SentTime == null &amp;&amp; AttemptCount &lt; MaxAttempts</c>
/// 的行数（与 <see cref="OutboxRelayService{TContext}.ProcessBatchAsync"/> 的候选查询一致，
/// 排除已超 <c>MaxAttempts</c> 的毒消息 —— 那些是另一类运维问题，不应让积压计数虚高）。
/// </para>
/// <para>
/// 阈值判定（见 <see cref="ObservabilityOptions"/>）：
/// <list type="bullet">
/// <item>积压 &lt; <c>OutboxDegradedThreshold</c> → <c>Healthy</c></item>
/// <item>积压 ∈ [<c>Degraded</c>, <c>Unhealthy</c>) → <c>Degraded</c>（投递滞后，可能消费方变慢/宕机）</item>
/// <item>积压 ≥ <c>OutboxUnhealthyThreshold</c> → <c>Unhealthy</c>（积压严重，readiness 应摘流）</item>
/// </list>
/// </para>
/// </summary>
/// <typeparam name="TContext">应用 DbContext 类型。</typeparam>
public sealed class OutboxHealthCheck<TContext> : IHealthCheck
    where TContext : DbContext
{
    /// <summary>
    /// 未注册 <c>IOptions&lt;OutboxRelayOptions&gt;</c> 时的回退最大重试次数（= <see cref="OutboxRelayOptions"/> 默认值）。
    /// 用 const 避免 HealthCheck（Singleton）每次 DI 解析分配一个临时 options 实例。
    /// </summary>
    private const int DefaultMaxAttempts = 8;

    private readonly IDbContextFactory<TContext> _factory;
    private readonly ObservabilityOptions _options;
    private readonly int _maxAttempts;

    /// <summary>构造。</summary>
    /// <param name="relayOptions">
    /// 可选 —— DomainEvents 模块注册。未注册时回退到 <see cref="DefaultMaxAttempts"/>
    /// （= <see cref="OutboxRelayOptions"/> 默认值），让单启用 Observability 而不启用 DomainEvents 的项目仍可用。
    /// </param>
    public OutboxHealthCheck(
        IDbContextFactory<TContext> factory,
        IOptions<ObservabilityOptions> options,
        IOptions<OutboxRelayOptions>? relayOptions = null)
    {
        _factory = factory;
        _options = options.Value;
        _maxAttempts = relayOptions?.Value.MaxAttempts ?? DefaultMaxAttempts;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var dc = await _factory.CreateDbContextAsync(cancellationToken);
            var maxAttempts = _maxAttempts;
            var backlog = await dc.Set<OutboxMessage>()
                .CountAsync(m => m.SentTime == null && m.AttemptCount < maxAttempts, cancellationToken);

            var (status, msg) = backlog switch
            {
                _ when backlog >= _options.OutboxUnhealthyThreshold =>
                    (HealthStatus.Unhealthy, $"Outbox 积压严重：{backlog}（阈值 ≥ {_options.OutboxUnhealthyThreshold}）"),
                _ when backlog >= _options.OutboxDegradedThreshold =>
                    (HealthStatus.Degraded, $"Outbox 积压偏高：{backlog}（阈值 ≥ {_options.OutboxDegradedThreshold}）"),
                _ => (HealthStatus.Healthy, $"Outbox 积压正常：{backlog}"),
            };

            var data = new Dictionary<string, object> { ["backlog"] = backlog, ["maxAttempts"] = maxAttempts };
            return new HealthCheckResult(status, msg, data: data);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Outbox 积压查询失败", ex);
        }
    }
}
