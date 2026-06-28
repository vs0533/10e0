using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenE0.Core.Events.Outbox;

namespace TenE0.Core.Messaging.Kafka;

/// <summary>
/// Kafka 版 <see cref="IOutboxPublisher"/>（issue #165）。
///
/// <para>
/// 把 <see cref="OutboxMessage"/> 投递到 Kafka topic。失败抛异常（不吞），交由
/// <c>OutboxRelayService</c> 重试 —— <c>MaxAttempts</c> 兜底，绝不丢消息。
/// </para>
///
/// <para>
/// 投递语义：
/// <list type="bullet">
/// <item><b>幂等键</b>：<c>Key = OutboxMessage.Id</c> —— 同 key 进同 partition，保证单事件有序 + 下游可去重。</item>
/// <item><b>路由头</b>：<c>Headers{eventType, occurredAt}</c> —— 让单 topic 多事件类型可在消费端按头过滤。</item>
/// <item><b>可靠性</b>：<c>Acks.All</c>（全 ISR ACK）+ <c>EnableIdempotence=true</c>（broker 侧去重），
///   配合 Outbox 至少一次 + 下游幂等 = exactly-once。</item>
/// <item><b>投递确认</b>：<c>IProducer&lt;TKey,TValue&gt;.ProduceAsync</c> 返回的 <see cref="DeliveryResult{TKey,TValue}"/>
///   只有 <see cref="PersistenceStatus.Persisted"/> 才算成功落盘；否则抛异常让 Relay 重试。</item>
/// </list>
/// </para>
/// </summary>
public sealed class KafkaPublisher : IOutboxPublisher, IAsyncDisposable
{
    private readonly IKafkaProducerManager _producerManager;
    private readonly KafkaOptions _options;
    private readonly ILogger<KafkaPublisher> _logger;

    /// <summary>构造。注册为 <c>Scoped</c>（与 IOutboxPublisher 默认生命周期一致）。</summary>
    public KafkaPublisher(
        IKafkaProducerManager producerManager,
        IOptions<KafkaOptions> options,
        ILogger<KafkaPublisher> logger)
    {
        _producerManager = producerManager;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var topic = _producerManager.ResolveTopic(message.EventType);
        var kafkaMessage = new Message<string, string>
        {
            Key = message.Id,                       // 幂等键：同 key → 同 partition → 单事件有序 + 下游去重
            Value = message.Payload,                // Outbox 已是 JSON 串，直接透传
            Headers = new Headers
            {
                { "eventType", Encoding.UTF8.GetBytes(message.EventType) },
                { "occurredAt", Encoding.UTF8.GetBytes(message.OccurredOn.ToString("O")) },
            },
        };

        try
        {
            var result = await _producerManager.Producer.ProduceAsync(topic, kafkaMessage, cancellationToken)
                .ConfigureAwait(false);

            // 仅 Persisted 视为成功（全 ISR 持久化）。其它状态（PossiblyPersisted 等）抛异常让 Relay 重试
            // —— 重试幂等（同 key + EnableIdempotence），不会造成下游重复。
            if (result.Status != PersistenceStatus.Persisted)
                throw new InvalidOperationException(
                    $"Kafka 投递未持久化 Id={message.Id} Topic={topic} Status={result.Status}，将由 Relay 重试。");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Kafka 投递失败 Id={Id} EventType={EventType} Topic={Topic}，抛出交由 Outbox Relay 重试",
                message.Id, message.EventType, topic);
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
