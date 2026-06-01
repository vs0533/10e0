using System.Text.Json;

namespace TenE0.Core.Events.Outbox;

/// <summary>
/// IOutboxPublisher 的默认进程内实现：
/// 反序列化 JSON Payload 为强类型 IDomainEvent，调 IDomainEventDispatcher 分发到本进程内的 Handler。
///
/// 适用场景：
/// - 单体应用（命令产生事件，订阅者在同一进程内处理）
/// - 开发/测试环境
///
/// 不适用：
/// - 跨服务异步通信（请替换为 Kafka/RabbitMQ/CAP 实现）
/// </summary>
internal sealed class InProcessOutboxPublisher(IDomainEventDispatcher dispatcher) : IOutboxPublisher
{
    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var type = Type.GetType(message.EventType)
            ?? throw new InvalidOperationException(
                $"无法解析事件 CLR 类型：{message.EventType}。" +
                $"可能原因：事件类型已重命名或所在程序集未加载。");

        if (JsonSerializer.Deserialize(message.Payload, type) is not IDomainEvent domainEvent)
            throw new InvalidOperationException(
                $"事件反序列化结果不是 IDomainEvent：{message.EventType}");

        await dispatcher.DispatchAsync(domainEvent, cancellationToken);
    }
}
