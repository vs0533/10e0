using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TenE0.Core.Events.Outbox;
using TenE0.Core.Messaging.Kafka;

namespace TenE0.Core.Tests.Messaging.Kafka;

/// <summary>
/// #165 KafkaPublisher 单测。
/// 验证：成功路径 ProduceAsync 调用 + Key/Value/Headers 正确；非 Persisted 抛异常；
/// ProduceAsync 异常传播；topic 解析。
/// </summary>
[Trait("Category", "Unit")]
public sealed class KafkaPublisherTests
{
    private static OutboxMessage NewMessage(string? eventType = null, string id = "msg-1") => new()
    {
        Id = id,
        EventType = eventType ?? "TenE0.Demo.OrderCreatedEvent, TenE0.Demo",
        Payload = """{"orderId":"o-1","amount":100}""",
        OccurredOn = new DateTimeOffset(2026, 6, 28, 12, 0, 0, TimeSpan.Zero),
    };

    private static (Mock<IProducer<string, string>> Producer, Mock<IKafkaProducerManager> Mgr) CreateMocks()
    {
        var producerMock = new Mock<IProducer<string, string>>();
        var mgrMock = new Mock<IKafkaProducerManager>();
        mgrMock.SetupGet(m => m.Producer).Returns(producerMock.Object);
        mgrMock.Setup(m => m.ResolveTopic(It.IsAny<string>()))
            .Returns<string>(_ => "tene0.domain-events");
        return (producerMock, mgrMock);
    }

    private static KafkaPublisher CreatePublisher(Mock<IKafkaProducerManager> mgr, KafkaOptions? options = null)
        => new(mgr.Object, Options.Create(options ?? new KafkaOptions()),
            NullLogger<KafkaPublisher>.Instance);

    private static DeliveryResult<string, string> DeliveryResult(
        PersistenceStatus status = PersistenceStatus.Persisted)
        => new()
        {
            Status = status,
            Partition = new Partition(0),
            Offset = new Offset(0),
            Topic = "tene0.domain-events",
            Message = new Message<string, string> { Key = "msg-1", Value = "{}" },
        };

    [Fact]
    public async Task PublishAsync_Persisted_ReturnsWithoutThrowing()
    {
        var (producerMock, mgrMock) = CreateMocks();
        producerMock
            .Setup(p => p.ProduceAsync(
                It.IsAny<string>(), It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DeliveryResult(PersistenceStatus.Persisted));

        var sut = CreatePublisher(mgrMock);

        var act = () => sut.PublishAsync(NewMessage(), CancellationToken.None);

        await act.Should().NotThrowAsync();
        producerMock.Verify(
            p => p.ProduceAsync(
                "tene0.domain-events",
                It.IsAny<Message<string, string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PublishAsync_SetsKeyValueAndHeaders()
    {
        var (producerMock, mgrMock) = CreateMocks();
        Message<string, string>? captured = null;
        producerMock
            .Setup(p => p.ProduceAsync(
                It.IsAny<string>(), It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, Message<string, string>, CancellationToken>((_, msg, _) => captured = msg)
            .ReturnsAsync(DeliveryResult());

        var sut = CreatePublisher(mgrMock);
        var msg = NewMessage();

        await sut.PublishAsync(msg, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Key.Should().Be("msg-1");          // 幂等键 = OutboxMessage.Id
        captured.Value.Should().Be(msg.Payload);     // Payload 直接透传

        captured.Headers.Should().NotBeNull();
        // Headers 含 eventType + occurredAt
        var eventTypeHeader = captured.Headers.GetLastBytes("eventType");
        System.Text.Encoding.UTF8.GetString(eventTypeHeader)
            .Should().Be(msg.EventType);
        var occurredAtHeader = captured.Headers.GetLastBytes("occurredAt");
        System.Text.Encoding.UTF8.GetString(occurredAtHeader)
            .Should().Be(msg.OccurredOn.ToString("O"));
    }

    [Theory]
    [InlineData(PersistenceStatus.PossiblyPersisted)]
    [InlineData(PersistenceStatus.NotPersisted)]
    public async Task PublishAsync_NonPersistedStatus_Throws(PersistenceStatus status)
    {
        var (producerMock, mgrMock) = CreateMocks();
        producerMock
            .Setup(p => p.ProduceAsync(
                It.IsAny<string>(), It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DeliveryResult(status));

        var sut = CreatePublisher(mgrMock);

        var act = () => sut.PublishAsync(NewMessage(), CancellationToken.None);

        // 非 Persisted 必须抛 —— OutboxRelayService 据此重试，重试幂等不会造成下游重复。
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*未持久化*");
    }

    [Fact]
    public async Task PublishAsync_ProduceThrows_PropagatesException()
    {
        var (producerMock, mgrMock) = CreateMocks();
        producerMock
            .Setup(p => p.ProduceAsync(
                It.IsAny<string>(), It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KafkaException(new Error(ErrorCode.BrokerNotAvailable, "broker 宕机")));

        var sut = CreatePublisher(mgrMock);

        var act = () => sut.PublishAsync(NewMessage(), CancellationToken.None);

        await act.Should().ThrowAsync<KafkaException>();
    }

    [Fact]
    public async Task PublishAsync_TopicResolverUsed()
    {
        var producerMock = new Mock<IProducer<string, string>>();
        var mgrMock = new Mock<IKafkaProducerManager>();
        mgrMock.SetupGet(m => m.Producer).Returns(producerMock.Object);
        mgrMock.Setup(m => m.ResolveTopic(It.IsAny<string>()))
            .Returns<string>(et => $"tene0.{et.Split('.')[1]?.ToLowerInvariant()}");

        producerMock
            .Setup(p => p.ProduceAsync(
                It.IsAny<string>(), It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DeliveryResult());

        var sut = CreatePublisher(mgrMock);

        await sut.PublishAsync(NewMessage("TenE0.Audit.OrderCreated"), CancellationToken.None);

        producerMock.Verify(
            p => p.ProduceAsync(
                "tene0.audit",
                It.IsAny<Message<string, string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PublishAsync_CancellationToken_PassedThrough()
    {
        var (producerMock, mgrMock) = CreateMocks();
        CancellationToken capturedToken = default;
        producerMock
            .Setup(p => p.ProduceAsync(
                It.IsAny<string>(), It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, Message<string, string>, CancellationToken>((_, _, ct) => capturedToken = ct)
            .ReturnsAsync(DeliveryResult());

        var sut = CreatePublisher(mgrMock);
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        await sut.PublishAsync(NewMessage(), token);

        capturedToken.Should().Be(token);
    }
}
