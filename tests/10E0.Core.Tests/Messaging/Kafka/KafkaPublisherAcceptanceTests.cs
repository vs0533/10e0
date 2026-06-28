using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text;
using Testcontainers.Kafka;
using TenE0.Core.Events.Outbox;
using TenE0.Core.Messaging.Kafka;

namespace TenE0.Core.Tests.Messaging.Kafka;

/// <summary>
/// #165 KafkaPublisher 集成测试（Testcontainers 起 Kafka broker）。
///
/// <b>CI 注意</b>：本测试需要 Docker。本地无 Docker 时 loud-fail（与 OutboxRelayConcurrencyTests 惯例一致）。
/// CI runner 无 Docker 时按 Acceptance trait 过滤排除。
/// </summary>
[Trait("Category", "Acceptance")]
[Trait("Requires", "Docker")]
public sealed class KafkaPublisherAcceptanceTests : IAsyncLifetime
{
    private readonly KafkaContainer _container = new KafkaBuilder()
        .WithImage("confluentinc/cp-kafka:7.6.1")
        .Build();

    private string _bootstrapServers = "";
    private bool _started;

    public async Task InitializeAsync()
    {
        try
        {
            await _container.StartAsync();
            _started = true;
            _bootstrapServers = _container.GetBootstrapAddress();
        }
        catch
        {
            // Docker 不可用 → 留 false，测试方法内 loud-fail。
            _started = false;
        }
    }

    public async Task DisposeAsync()
    {
        try { await _container.DisposeAsync(); }
        catch { /* 容器清理失败忽略 */ }
    }

    [Fact]
    public async Task PublishAsync_RealBroker_MessageConsumable()
    {
        if (!_started)
        {
            Assert.Fail(
                "Requires Docker daemon. Test uses Testcontainers.Kafka to spin up real Kafka. "
                + "Run on a machine with Docker, or filter out with "
                + "`--filter \"Category!=Acceptance\"`.");
        }

        const string topic = "tene0.domain-events";
        const string consumerGroup = "acceptance-test-cg";

        // Arrange：用真实 KafkaProducerManager 端到端验证（Producer 构建 + produce）。
        var options = new KafkaOptions
        {
            BootstrapServers = _bootstrapServers,
            Topic = topic,
            MessageTimeout = TimeSpan.FromSeconds(10),
        };
        var mgr = new KafkaProducerManager(Options.Create(options), NullLogger<KafkaProducerManager>.Instance);
        var publisher = new KafkaPublisher(
            mgr, Options.Create(options), NullLogger<KafkaPublisher>.Instance);

        // 先起 consumer 并订阅 topic（确保有消费者后再 produce，offset 不丢）。
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = _bootstrapServers,
            GroupId = consumerGroup,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,
        };
        using var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();
        consumer.Subscribe(topic);

        var msg = new OutboxMessage
        {
            Id = "accept-msg-1",
            EventType = "TenE0.Tests.OrderCreatedEvent",
            Payload = """{"orderId":"o-accept","amount":42}""",
            OccurredOn = DateTimeOffset.UtcNow,
        };

        // Act
        await publisher.PublishAsync(msg, CancellationToken.None);

        // Assert：从 topic 消费回消息，校验 key/value/headers
        ConsumeResult<string, string>? result = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                result = consumer.Consume(TimeSpan.FromSeconds(2));
                if (result is not null) break;
            }
            catch (ConsumeException) { /* 短暂重试 */ }
        }

        result.Should().NotBeNull("消息应在投递后可被消费");
        result!.Message.Key.Should().Be("accept-msg-1");
        result.Message.Value.Should().Be(msg.Payload);

        var eventTypeHeader = result.Message.Headers.GetLastBytes("eventType");
        Encoding.UTF8.GetString(eventTypeHeader).Should().Be(msg.EventType);

        await mgr.DisposeAsync();
        consumer.Close();
    }
}
