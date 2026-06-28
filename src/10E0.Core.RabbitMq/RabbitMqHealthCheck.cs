using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using TenE0.Core.DependencyInjection;

namespace TenE0.Core.Messaging.RabbitMq;

/// <summary>
/// RabbitMQ 连通性健康检查（issue #165）。
///
/// <para>
/// 由 <see cref="RabbitMqExtensions"/> 的 <c>AddTenE0RabbitMqPublisher</c> 注册，带 <see cref="ObservabilityExtensions.ReadyTag"/>
/// 标签，纳入 <c>/health/ready</c> 探针（K8s readiness）。broker 断线时报告 <c>Unhealthy</c>，
/// 触发摘流，避免把流量打到无法投递事件的实例（Outbox 会积压，但积压是 <c>OutboxHealthCheck</c> 的职责）。
/// </para>
/// </summary>
public sealed class RabbitMqHealthCheck : IHealthCheck
{
    private readonly IRabbitMqConnectionManager _connectionManager;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqHealthCheck> _logger;

    /// <summary>构造。</summary>
    public RabbitMqHealthCheck(
        IRabbitMqConnectionManager connectionManager,
        Microsoft.Extensions.Options.IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqHealthCheck> logger)
    {
        _connectionManager = connectionManager;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // 连接池无可用连接（重连期间或未初始化）直接判不可用 —— 避免后续探测无谓建连。
        if (!_connectionManager.IsConnected)
            return HealthCheckResult.Unhealthy("RabbitMQ 连接不可用（未建立或重连中）");

        try
        {
            // 借一个 channel 做轻量探活：passive declare 一个临时队列，成功即说明 broker 可达 + 可建 channel。
            // passive（declare-passive）= 仅检查存在性不创建；用临时名避免副作用。
            await using var lease = await _connectionManager.GetChannelAsync(cancellationToken).ConfigureAwait(false);
            // target exchange 存在性探测：passive declare 等价于 "exchange exists?" 检查，不创建、无副作用。
            await lease.Channel.ExchangeDeclarePassiveAsync(_options.Exchange.Name, cancellationToken)
                .ConfigureAwait(false);

            return HealthCheckResult.Healthy("RabbitMQ 连接与交换机正常");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RabbitMQ 健康检查探测失败");
            return HealthCheckResult.Unhealthy("RabbitMQ 探测失败", ex);
        }
    }
}
