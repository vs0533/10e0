using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using System.Text;
using Testcontainers.RabbitMq;
using TenE0.Core.Events.Outbox;
using TenE0.Core.Messaging.RabbitMq;

namespace TenE0.Core.Tests.Messaging.RabbitMq;

/// <summary>
/// #165 RabbitMqPublisher 集成测试（Testcontainers 起 RabbitMQ broker）。
///
/// <b>CI 注意</b>：本测试需要 Docker。本地无 Docker 时 loud-fail（与 OutboxRelayConcurrencyTests 惯例一致），
/// 不静默跳过以免给 review 假象。CI runner 无 Docker 时按 Acceptance trait 过滤排除。
/// </summary>
[Trait("Category", "Acceptance")]
[Trait("Requires", "Docker")]
public sealed class RabbitMqPublisherAcceptanceTests : IAsyncLifetime
{
    private readonly RabbitMqContainer _container = new RabbitMqBuilder()
        .WithImage("rabbitmq:3.13-management")
        .WithUsername("acceptance")
        .WithPassword("acceptance")
        .Build();

    private RabbitMqOptions _options = new();
    private bool _started;

    public async Task InitializeAsync()
    {
        try
        {
            await _container.StartAsync();
            _started = true;
            // 真实 ConnectionManager 用 host/port 配置（与生产用法一致）。
            // 凭据是 builder 显式设的 acceptance/acceptance（容器不暴露属性）。
            _options = new RabbitMqOptions
            {
                Connection =
                {
                    HostName = _container.Hostname,
                    Port = _container.GetMappedPublicPort(5672),
                    UserName = "acceptance",
                    Password = "acceptance",
                    MaxConnections = 2,
                },
            };
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
    public async Task PublishAsync_RealBroker_MessageDeliverable()
    {
        if (!_started)
        {
            Assert.Fail(
                "Requires Docker daemon. Test uses Testcontainers.RabbitMq to spin up real RabbitMQ. "
                + "Run on a machine with Docker, or filter out with "
                + "`--filter \"Category!=Acceptance\"`.");
        }

        // Arrange：用真实 ConnectionManager 端到端验证（连接池 + channel lease + publish）。
        var mgr = new RabbitMqConnectionManager(
            Options.Create(_options), NullLogger<RabbitMqConnectionManager>.Instance);
        var publisher = new RabbitMqPublisher(
            mgr, Options.Create(_options), NullLogger<RabbitMqPublisher>.Instance);

        var msg = new OutboxMessage
        {
            Id = "accept-msg-1",
            EventType = "TenE0.Tests.OrderCreatedEvent",
            Payload = """{"orderId":"o-accept","amount":42}""",
            OccurredOn = DateTimeOffset.UtcNow,
        };

        // 用独立的 admin 连接绑定临时队列消费验证（不经过被测的 ConnectionManager）。
        var factory = new ConnectionFactory
        {
            HostName = _options.Connection.HostName,
            Port = _options.Connection.Port,
            UserName = "acceptance",
            Password = "acceptance",
        };
        await using var adminConn = await factory.CreateConnectionAsync();
        await using var adminChannel = await adminConn.CreateChannelAsync();

        const string queueName = "acceptance-test-q";
        // 先确保 exchange 存在（幂等 declare），再绑队列 —— 避免 QueueBind 时 exchange 未建。
        await adminChannel.ExchangeDeclareAsync(
            _options.Exchange.Name, _options.Exchange.Type, durable: _options.Exchange.Durable, autoDelete: false);
        await adminChannel.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: true);
        await adminChannel.QueueBindAsync(queueName, _options.Exchange.Name, routingKey: "#");

        // Act
        await publisher.PublishAsync(msg, CancellationToken.None);

        // Assert：从队列取回消息，校验 body + MessageId（幂等键）+ Type + Persistent
        BasicGetResult? result;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        do
        {
            result = await adminChannel.BasicGetAsync(queueName, autoAck: true);
            if (result is null) await Task.Delay(100);
        } while (result is null && DateTime.UtcNow < deadline);

        result.Should().NotBeNull("消息应在投递后到达绑定的队列");
        Encoding.UTF8.GetString(result!.Body.ToArray()).Should().Be(msg.Payload);
        result.BasicProperties.MessageId.Should().Be("accept-msg-1");
        result.BasicProperties.Type.Should().Be(msg.EventType);
        result.BasicProperties.Persistent.Should().BeTrue();
    }
}
