using System.Text.Json;
using TenE0.Core.Events;
using TenE0.Core.Events.Outbox;

namespace TenE0.Core.Tests.Events.Outbox;

public sealed class InProcessOutboxPublisherTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task PublishAsync_ValidMessage_DispatchesEvent()
    {
        var testEvent = new TestOutboxEvent("hello");
        var eventType = typeof(TestOutboxEvent).AssemblyQualifiedName!;
        var payload = JsonSerializer.Serialize(testEvent);
        var message = new OutboxMessage
        {
            EventType = eventType,
            Payload = payload,
            OccurredOn = DateTimeOffset.UtcNow,
        };

        var dispatcherMock = new Mock<IDomainEventDispatcher>();
        IDomainEvent? captured = null;
        dispatcherMock
            .Setup(d => d.DispatchAsync(It.IsAny<IDomainEvent>(), It.IsAny<CancellationToken>()))
            .Callback<IDomainEvent, CancellationToken>((evt, _) => captured = evt);

        var sut = new InProcessOutboxPublisher(dispatcherMock.Object);

        using var cts = new CancellationTokenSource();
        await sut.PublishAsync(message, cts.Token);

        captured.Should().NotBeNull().And.BeOfType<TestOutboxEvent>();
        ((TestOutboxEvent)captured!).Data.Should().Be("hello");
        dispatcherMock.Verify(d => d.DispatchAsync(It.IsAny<IDomainEvent>(), cts.Token), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PublishAsync_UnknownEventType_ThrowsInvalidOperation()
    {
        var message = new OutboxMessage
        {
            EventType = "TenE0.Core.Events.NonExistentEvent, NonExistentAssembly",
            Payload = "{}",
            OccurredOn = DateTimeOffset.UtcNow,
        };

        var dispatcherMock = new Mock<IDomainEventDispatcher>();
        var sut = new InProcessOutboxPublisher(dispatcherMock.Object);

        var act = () => sut.PublishAsync(message, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.WithMessage("*NonExistentEvent*");
        dispatcherMock.Verify(d => d.DispatchAsync(It.IsAny<IDomainEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PublishAsync_NonDomainEventPayload_ThrowsInvalidOperation()
    {
        var notADomainEvent = new NonDomainEvent("test");
        var eventType = typeof(NonDomainEvent).AssemblyQualifiedName!;
        var payload = JsonSerializer.Serialize(notADomainEvent);
        var message = new OutboxMessage
        {
            EventType = eventType,
            Payload = payload,
            OccurredOn = DateTimeOffset.UtcNow,
        };

        var dispatcherMock = new Mock<IDomainEventDispatcher>();
        var sut = new InProcessOutboxPublisher(dispatcherMock.Object);

        var act = () => sut.PublishAsync(message, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.WithMessage("*IDomainEvent*");
        dispatcherMock.Verify(d => d.DispatchAsync(It.IsAny<IDomainEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PublishAsync_InvalidJson_ThrowsJsonException()
    {
        var eventType = typeof(TestOutboxEvent).AssemblyQualifiedName!;
        var message = new OutboxMessage
        {
            EventType = eventType,
            Payload = "{invalid json!!!}",
            OccurredOn = DateTimeOffset.UtcNow,
        };

        var dispatcherMock = new Mock<IDomainEventDispatcher>();
        var sut = new InProcessOutboxPublisher(dispatcherMock.Object);

        var act = () => sut.PublishAsync(message, CancellationToken.None);

        await act.Should().ThrowAsync<JsonException>();
        dispatcherMock.Verify(d => d.DispatchAsync(It.IsAny<IDomainEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PublishAsync_CancellationToken_PassedThrough()
    {
        var testEvent = new TestOutboxEvent("token-test");
        var eventType = typeof(TestOutboxEvent).AssemblyQualifiedName!;
        var payload = JsonSerializer.Serialize(testEvent);
        var message = new OutboxMessage
        {
            EventType = eventType,
            Payload = payload,
            OccurredOn = DateTimeOffset.UtcNow,
        };

        var dispatcherMock = new Mock<IDomainEventDispatcher>();
        CancellationToken capturedToken = default;
        dispatcherMock
            .Setup(d => d.DispatchAsync(It.IsAny<IDomainEvent>(), It.IsAny<CancellationToken>()))
            .Callback<IDomainEvent, CancellationToken>((_, ct) => capturedToken = ct);

        var sut = new InProcessOutboxPublisher(dispatcherMock.Object);

        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        await sut.PublishAsync(message, token);

        capturedToken.Should().Be(token);
        dispatcherMock.Verify(d => d.DispatchAsync(It.IsAny<IDomainEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed record TestOutboxEvent(string Data) : IDomainEvent;

    private sealed record NonDomainEvent(string Data);
}
