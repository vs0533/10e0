using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using TenE0.Core.Events.Outbox;
using TenE0.Core.Messaging.RabbitMq;

namespace TenE0.Core.Tests.Messaging.RabbitMq;

/// <summary>
/// #165 RabbitMqPublisher 单测。
///
/// 验证：成功路径调 BasicPublishAsync + props 设置正确；channel 异常时抛出（交由 Relay 重试）；
/// CancellationToken 透传。不连真实 broker —— mock IRabbitMqConnectionManager 返回带 fake channel 的 lease。
/// </summary>
[Trait("Category", "Unit")]
public sealed class RabbitMqPublisherTests
{
    private static OutboxMessage NewMessage(string? eventType = null, string id = "msg-1") => new()
    {
        Id = id,
        EventType = eventType ?? "TenE0.Demo.OrderCreatedEvent, TenE0.Demo",
        Payload = """{"orderId":"o-1","amount":100}""",
        OccurredOn = new DateTimeOffset(2026, 6, 28, 12, 0, 0, TimeSpan.Zero),
    };

    private static RabbitMqPublisher CreatePublisher(
        Mock<IRabbitMqConnectionManager> connMgr,
        RabbitMqOptions? options = null)
    {
        var opt = options ?? new RabbitMqOptions();
        return new RabbitMqPublisher(connMgr.Object, Options.Create(opt), NullLogger<RabbitMqPublisher>.Instance);
    }

    /// <summary>构造一个 mock channel + lease，Setup BasicPublishAsync 成功完成。</summary>
    private static (Mock<IChannel> Channel, Mock<IRabbitMqConnectionManager> ConnMgr) CreateConnectedMocks()
    {
        var channelMock = new Mock<IChannel>();

        // lease DisposeAsync 会调 channel.DisposeAsync + 回调；回调在此返回 ValueTask。
        var connMgrMock = new Mock<IRabbitMqConnectionManager>();
        connMgrMock
            .Setup(m => m.GetChannelAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RabbitMqChannelLease(
                channelMock.Object,
                () => ValueTask.CompletedTask));

        return (channelMock, connMgrMock);
    }

    [Fact]
    public async Task PublishAsync_Success_CallsBasicPublishWithCorrectProps()
    {
        var (channelMock, connMgrMock) = CreateConnectedMocks();
        BasicProperties? capturedProps = null;
        string? capturedExchange = null;
        string? capturedRoutingKey = null;
        ReadOnlyMemory<byte> capturedBody = default;

        channelMock
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken>(
                (ex, rk, _, props, body, _) =>
                {
                    capturedExchange = ex;
                    capturedRoutingKey = rk;
                    capturedProps = props as BasicProperties;
                    capturedBody = body;
                })
            .Returns(new ValueTask());

        var sut = CreatePublisher(connMgrMock);
        var msg = NewMessage();

        await sut.PublishAsync(msg, CancellationToken.None);

        // exchange + routing key（routing key = 事件类型）
        capturedExchange.Should().Be("tene0.domain-events");
        capturedRoutingKey.Should().Be(msg.EventType);

        // props：持久化 + 幂等键 + 类型 + contentType + 时间戳
        capturedProps.Should().NotBeNull();
        capturedProps!.Persistent.Should().BeTrue();
        capturedProps.MessageId.Should().Be("msg-1");
        capturedProps.Type.Should().Be(msg.EventType);
        capturedProps.ContentType.Should().Be("application/json");
        capturedProps.Timestamp.UnixTime
            .Should().Be(msg.OccurredOn.ToUnixTimeSeconds());

        // body = Payload 的 UTF8
        capturedBody.ToArray()
            .Should().Equal(System.Text.Encoding.UTF8.GetBytes(msg.Payload));
    }

    [Fact]
    public async Task PublishAsync_ChannelThrows_PropagatesException()
    {
        var (channelMock, connMgrMock) = CreateConnectedMocks();
        channelMock
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("broker 不可达"));

        var sut = CreatePublisher(connMgrMock);

        var act = () => sut.PublishAsync(NewMessage(), CancellationToken.None);

        // 失败必须抛异常 —— OutboxRelayService 据此重试，吞异常会导致消息永远卡在未发送。
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*broker 不可达*");
    }

    [Fact]
    public async Task PublishAsync_GetChannelThrows_PropagatesException()
    {
        var connMgrMock = new Mock<IRabbitMqConnectionManager>();
        connMgrMock
            .Setup(m => m.GetChannelAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("连接池耗尽 / 重连中"));

        var sut = CreatePublisher(connMgrMock);

        var act = () => sut.PublishAsync(NewMessage(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*连接池耗尽*");
    }

    [Fact]
    public async Task PublishAsync_CustomExchange_UsedInPublish()
    {
        var (channelMock, connMgrMock) = CreateConnectedMocks();
        string? capturedExchange = null;
        channelMock
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken>(
                (ex, _, _, _, _, _) => capturedExchange = ex)
            .Returns(new ValueTask());

        var sut = CreatePublisher(connMgrMock, new RabbitMqOptions
        {
            Exchange = { Name = "custom.events", Type = "direct" },
        });

        await sut.PublishAsync(NewMessage(), CancellationToken.None);

        capturedExchange.Should().Be("custom.events");
    }

    [Fact]
    public async Task PublishAsync_CancellationToken_PassedThrough()
    {
        var (channelMock, connMgrMock) = CreateConnectedMocks();
        CancellationToken capturedToken = default;
        channelMock
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken>(
                (_, _, _, _, _, ct) => capturedToken = ct)
            .Returns(new ValueTask());

        var sut = CreatePublisher(connMgrMock);
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        await sut.PublishAsync(NewMessage(), token);

        capturedToken.Should().Be(token);
    }

    [Fact]
    public async Task PublishAsync_DisposesChannelLease()
    {
        var (channelMock, connMgrMock) = CreateConnectedMocks();
        channelMock
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns(new ValueTask());

        var sut = CreatePublisher(connMgrMock);

        await sut.PublishAsync(NewMessage(), CancellationToken.None);

        // lease 释放时应调 channel.DisposeAsync（避免 channel 泄漏）。
        channelMock.Verify(c => c.DisposeAsync(), Times.Once);
    }
}
