using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TenE0.Core.Messaging.Kafka;

/// <summary>
/// Kafka Producer 管理器契约（issue #165）。
///
/// <para>
/// 抽成接口让 <see cref="KafkaPublisher"/> 可在单测中 mock（避免依赖真实 broker）。
/// 生产实现 <see cref="KafkaProducerManager"/> 由 DI 注入。
/// </para>
/// </summary>
public interface IKafkaProducerManager
{
    /// <summary>获取（懒初始化的）Producer 实例。线程安全 —— 首次访问时建实例，之后复用。</summary>
    IProducer<string, string> Producer { get; }

    /// <summary>是否已建立 Producer（非精确连通性，供健康检查）。</summary>
    bool IsConnected { get; }

    /// <summary>解析目标 topic（应用 TopicResolver 或回退默认 Topic）。</summary>
    string ResolveTopic(string eventType);
}

/// <summary>
/// Kafka Producer 管理器（issue #165）。
///
/// <para>
/// <c>Confluent.Kafka</c> 的 <c>IProducer&lt;TKey,TValue&gt;</c> 本身就是<b>线程安全长连接</b>
///（底层 librdkafka 自动重连、连接池化），无需像 RabbitMQ 那样自管连接池。
/// 本类职责仅是：按配置创建唯一 Producer 实例（缓存复用，避免每事件新建 Producer 的开销）+
/// 暴露 <see cref="IsConnected"/> 供健康检查。
/// </para>
///
/// <para>
/// 注册为 <c>Singleton</c>：Producer 实例跨请求共享，进程生命周期内复用。
/// </para>
/// </summary>
public sealed class KafkaProducerManager : IKafkaProducerManager, IAsyncDisposable
{
    private readonly KafkaOptions _options;
    private readonly ILogger<KafkaProducerManager> _logger;
    private IProducer<string, string>? _producer;
    private readonly object _gate = new();
    private volatile bool _disposed;

    /// <summary>构造。</summary>
    public KafkaProducerManager(
        IOptions<KafkaOptions> options,
        ILogger<KafkaProducerManager> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// 获取（懒初始化的）Producer 实例。线程安全 —— 首次访问时建实例，之后复用。
    /// </summary>
    public IProducer<string, string> Producer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_producer is not null)
                return _producer;

            lock (_gate)
            {
                if (_producer is not null)
                    return _producer;

                var config = new ProducerConfig
                {
                    BootstrapServers = _options.BootstrapServers,
                    Acks = _options.Acks,
                    EnableIdempotence = _options.EnableIdempotence,
                    LingerMs = _options.LingerMs,
                    // librdkafka 的 message.timeout.ms：单条消息投递超时。
                    MessageTimeoutMs = (int)_options.MessageTimeout.TotalMilliseconds,
                };
                _producer = new ProducerBuilder<string, string>(config).Build();
                _logger.LogInformation(
                    "Kafka Producer 已建立 BootstrapServers={Servers} Topic={Topic} Acks={Acks} Idempotence={Idempotence}",
                    _options.BootstrapServers, _options.Topic, _options.Acks, _options.EnableIdempotence);
                return _producer;
            }
        }
    }

    /// <summary>
    /// 是否已建立 Producer（非精确连通性，供健康检查）。
    /// Kafka Producer 一旦构建即视为"已就绪"，真正连通性由健康检查的 metadata 探测决定。
    /// </summary>
    public bool IsConnected => _producer is not null && !_disposed;

    /// <summary>
    /// 解析目标 topic（应用 <see cref="KafkaOptions.TopicResolver"/> 或回退默认 <see cref="KafkaOptions.Topic"/>）。
    /// </summary>
    public string ResolveTopic(string eventType)
        => _options.TopicResolver?.Invoke(eventType) ?? _options.Topic;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        var p = _producer;
        if (p is not null)
        {
            // Flush 确保 Producer 内部缓冲的消息全部发出（带超时，避免 dispose 阻塞宿主关闭）。
            try { await Task.Run(() => p.Flush(TimeSpan.FromSeconds(10))); }
            catch { /* flush 失败忽略 —— 残留消息由 Outbox 重试兜底 */ }
            p.Dispose();
        }
    }
}
