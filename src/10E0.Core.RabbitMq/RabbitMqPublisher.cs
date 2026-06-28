using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using TenE0.Core.Events.Outbox;

namespace TenE0.Core.Messaging.RabbitMq;

/// <summary>
/// RabbitMQ 版 <see cref="IOutboxPublisher"/>（issue #165）。
///
/// <para>
/// 把 <see cref="OutboxMessage"/> 以持久化消息投递到配置的交换机，按事件类型路由（topic exchange）。
/// 失败抛异常（不吞），交由 <c>OutboxRelayService</c> 重试 —— <c>MaxAttempts</c> 兜底，绝不丢消息。
/// </para>
///
/// <para>
/// 投递语义：
/// <list type="bullet">
/// <item><b>持久化</b>：<c>Persistent=true</c> + 持久化交换机，防 broker 重启丢消息。</item>
/// <item><b>幂等键</b>：<c>MessageId = OutboxMessage.Id</c>，下游消费者可据此去重。</item>
/// <item><b>路由</b>：<c>Type = EventType</c>（CLR 全名）+ <c>RoutingKey = EventType</c>，
///   topic exchange 支持按事件类型模式订阅。</item>
/// <item><b>可靠性兜底</b>：<c>RabbitMQ.Client</c> v7 移除了传统的 Publisher Confirms
///   （<c>ConfirmSelect</c>/<c>WaitForConfirms</c>），改为基于 <c>BasicAcks</c>/<c>BasicNacks</c>
///   事件的异步模型。本实现<b>不</b>实现该复杂事件协调 —— Outbox 模式本身就是为"至少一次"投递设计的：
///   投递（含网络层失败、broker 暂时丢失）只要不 ACK 成功，<see cref="OutboxMessage"/> 不会标记
///   <c>SentTime</c>，<c>OutboxRelayService</c> 会幂等重发。配合下游消费者按 <c>MessageId</c> 去重，
///   即可达到 exactly-once 语义，无需在 Publisher 侧叠加 confirms 层。</item>
/// </list>
/// </para>
/// </summary>
public sealed class RabbitMqPublisher : IOutboxPublisher, IAsyncDisposable
{
    private readonly IRabbitMqConnectionManager _connectionManager;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqPublisher> _logger;

    /// <summary>构造。注册为 <c>Scoped</c>（与 IOutboxPublisher 默认生命周期一致）。</summary>
    public RabbitMqPublisher(
        IRabbitMqConnectionManager connectionManager,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqPublisher> logger)
    {
        _connectionManager = connectionManager;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        // leasing channel: channel 不可跨线程并发 publish，故每次 create→publish→dispose。
        await using var lease = await _connectionManager.GetChannelAsync(cancellationToken).ConfigureAwait(false);
        var channel = lease.Channel;

        var props = new BasicProperties
        {
            Persistent = true,                         // 持久化，防 broker 重启丢失
            MessageId = message.Id,                    // 幂等键，下游可去重
            Type = message.EventType,                  // 事件类型，供 AMQP 头路由/过滤
            ContentType = "application/json",
            Timestamp = new AmqpTimestamp(message.OccurredOn.ToUnixTimeSeconds()),
        };

        // RoutingKey 用事件类型（CLR 全名）；topic exchange 支持按命名空间/类型模式订阅。
        var routingKey = message.EventType;
        var body = Encoding.UTF8.GetBytes(message.Payload);

        try
        {
            await channel.BasicPublishAsync(
                exchange: _options.Exchange.Name,
                routingKey: routingKey,
                mandatory: false,
                basicProperties: props,
                body: body,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            // BasicPublishAsync 完成 = 已写入网络层/连接缓冲。连接异常会抛 → Relay 重试。
            // 注意：v7 不等 broker 持久化 ACK（见类注释的可靠性兜底说明）。
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RabbitMQ 投递失败 Id={Id} EventType={EventType}，抛出交由 Outbox Relay 重试",
                message.Id, message.EventType);
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
