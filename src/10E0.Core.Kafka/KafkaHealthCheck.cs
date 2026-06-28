using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenE0.Core.DependencyInjection;

namespace TenE0.Core.Messaging.Kafka;

/// <summary>
/// Kafka metadata 探测契约（issue #165）。
/// 抽成接口让 <see cref="KafkaHealthCheck"/> 单测可 mock（避免依赖真实 broker）。
/// </summary>
public interface IKafkaMetadataProbe
{
    /// <summary>请求指定 topic 的集群 metadata，返回 broker 数量；不可达抛异常。</summary>
    /// <param name="bootstrapServers">配置的 bootstrap servers。</param>
    /// <param name="topic">目标 topic。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>可达 broker 数量。</returns>
    Task<int> GetBrokerCountAsync(string bootstrapServers, string topic, CancellationToken cancellationToken);
}

/// <summary>
/// Kafka metadata 探测默认实现：用独立的 <c>AdminClient</c> 请求集群 metadata。
/// </summary>
internal sealed class KafkaMetadataProbe : IKafkaMetadataProbe
{
    /// <inheritdoc />
    public async Task<int> GetBrokerCountAsync(
        string bootstrapServers, string topic, CancellationToken cancellationToken)
    {
        var config = new AdminClientConfig { BootstrapServers = bootstrapServers };
        using var admin = new AdminClientBuilder(config).Build();
        // GetMetadata 是同步阻塞调用（v2.x 无 async 版），在 Task.Run 中跑避免阻塞 health 线程。
        // TimeSpan 兜底，避免 librdkafka 内部重试拖过 health 超时。
        var metadata = await Task.Run(
            () => admin.GetMetadata(topic, TimeSpan.FromSeconds(3)),
            cancellationToken).ConfigureAwait(false);
        return metadata.Brokers.Count;
    }
}

/// <summary>
/// Kafka 连通性健康检查（issue #165）。
///
/// <para>
/// 由 <see cref="KafkaExtensions"/> 的 <c>AddTenE0KafkaPublisher</c> 注册，带 <see cref="ObservabilityExtensions.ReadyTag"/>
/// 标签，纳入 <c>/health/ready</c> 探针（K8s readiness）。broker 不可达时报告 <c>Unhealthy</c>，触发摘流。
/// </para>
///
/// <para>
/// 探活方式：用独立的 <c>AdminClient</c> 请求集群 metadata（带超时），成功即说明至少一个 broker 可达。
/// 不复用 <see cref="KafkaProducerManager.Producer"/> —— Producer 是长连接，其健康不代表集群当前可达性；
/// 独立 AdminClient 每次新建轻量探测，结果更准确。
/// </para>
/// </summary>
public sealed class KafkaHealthCheck : IHealthCheck
{
    private readonly KafkaOptions _options;
    private readonly ILogger<KafkaHealthCheck> _logger;
    private readonly IKafkaMetadataProbe _probe;

    /// <summary>构造。DI 注册时 <paramref name="probe"/> 由容器注入（默认 <see cref="KafkaMetadataProbe"/>）。</summary>
    public KafkaHealthCheck(
        IOptions<KafkaOptions> options,
        ILogger<KafkaHealthCheck> logger,
        IKafkaMetadataProbe probe)
    {
        _options = options.Value;
        _logger = logger;
        _probe = probe;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var brokerCount = await _probe.GetBrokerCountAsync(
                _options.BootstrapServers, _options.Topic, cancellationToken).ConfigureAwait(false);

            return brokerCount > 0
                ? HealthCheckResult.Healthy($"Kafka 集群可达，brokers={brokerCount}")
                : HealthCheckResult.Unhealthy("Kafka metadata 返回 0 个 broker");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kafka 健康检查探测失败");
            return HealthCheckResult.Unhealthy("Kafka 探测失败", ex);
        }
    }
}
