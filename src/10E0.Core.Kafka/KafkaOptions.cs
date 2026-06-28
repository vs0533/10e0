using Confluent.Kafka;

namespace TenE0.Core.Messaging.Kafka;

/// <summary>
/// Kafka Publisher 配置（issue #165）。
///
/// <para>
/// 与 <c>OutboxRelayOptions</c> 平行的独立选项 —— Relay 只管"拉取批次 → 调投递器 → 重试"，
/// 投递机制细节（broker、topic、ack 级别）由本选项承载。
/// </para>
/// </summary>
public sealed class KafkaOptions
{
    /// <summary>配置节名。应用可从 <c>appsettings.json</c> 的 <c>"Kafka"</c> 段绑定。</summary>
    public const string SectionName = "Kafka";

    /// <summary>
    /// Bootstrap servers（逗号分隔，如 <c>kafka1:9092,kafka2:9092</c>）。默认 <c>localhost:9092</c>。
    /// </summary>
    public string BootstrapServers { get; set; } = "localhost:9092";

    /// <summary>
    /// 目标 topic。默认 <c>tene0.domain-events</c>。
    /// 单 topic 多事件类型 + <c>Headers[eventType]</c> 路由 —— 与 Outbox 的 <c>EventType</c> 一致。
    /// 需按事件类型分 topic 时，可在 <see cref="TopicResolver"/> 自定义。
    /// </summary>
    public string Topic { get; set; } = "tene0.domain-events";

    /// <summary>
    /// 自定义 topic 解析（按事件类型选 topic）。默认返回 <see cref="Topic"/>。
    /// 设此委托可把不同事件类型路由到不同 topic（如审计事件单独 topic）。
    /// </summary>
    public Func<string, string>? TopicResolver { get; set; }

    /// <summary>
    /// ACK 级别。默认 <see cref="Confluent.Kafka.Acks.All"/>（全 ISR ACK，防丢）。
    /// 性能优先且自担丢失风险可降为 <see cref="Confluent.Kafka.Acks.Leader"/> 或 <see cref="Confluent.Kafka.Acks.None"/>。
    /// </summary>
    public Acks Acks { get; set; } = Acks.All;

    /// <summary>
    /// 是否启用幂等 Producer（默认 <c>true</c>）。开启后 broker 侧去重，避免重试导致重复。
    /// 与 Outbox 的至少一次 + 下游幂等共同保证 exactly-once。
    /// </summary>
    public bool EnableIdempotence { get; set; } = true;

    /// <summary>
    /// Linger 时间（毫秒）。默认 <c>5</c>：Producer 攒 5ms 的消息批量发，提升吞吐。
    /// 延迟敏感可设 0（立即发）。
    /// </summary>
    public int LingerMs { get; set; } = 5;

    /// <summary>
    /// 单次 <c>ProduceAsync</c> 超时（<c>MessageTimeout</c>）。默认 5 秒 ——
    /// 超时抛异常让 Relay 重试（<c>MaxAttempts</c> 兜底）。
    /// </summary>
    public TimeSpan MessageTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
