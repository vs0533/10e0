using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TenE0.Core.Events;

namespace TenE0.Core.Tests.Events;

public sealed class InProcessDomainEventDispatcherTests
{
    private IDomainEventDispatcher CreateDispatcher<TEvent>(params IDomainEventHandler<TEvent>[] handlers)
        where TEvent : IDomainEvent
    {
        var services = new ServiceCollection();
        foreach (var handler in handlers)
        {
            services.AddSingleton<IDomainEventHandler<TEvent>>(handler);
        }
        var provider = services.BuildServiceProvider();
        return new InProcessDomainEventDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new NullLogger<InProcessDomainEventDispatcher>());
    }

    [Fact]
    public async Task Dispatch_NoHandlers_ShouldNotThrow()
    {
        var dispatcher = CreateDispatcher<DispatcherTestEvent>();
        var evt = new DispatcherTestEvent("no-handlers");

        var act = () => dispatcher.DispatchAsync(evt);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Dispatch_SingleHandler_ShouldExecute()
    {
        var evt = new DispatcherTestEvent("single");
        bool handlerCalled = false;

        var handler = new TestHandler(e =>
        {
            e.Should().BeSameAs(evt);
            handlerCalled = true;
        });

        var dispatcher = CreateDispatcher<DispatcherTestEvent>(handler);
        await dispatcher.DispatchAsync(evt);

        handlerCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Dispatch_MultipleHandlers_ShouldFanOut()
    {
        var evt = new DispatcherTestEvent("multi");
        bool h1 = false, h2 = false, h3 = false;

        var dispatcher = CreateDispatcher<DispatcherTestEvent>(
            new TestHandler(_ => h1 = true),
            new TestHandler(_ => h2 = true),
            new TestHandler(_ => h3 = true));

        await dispatcher.DispatchAsync(evt);

        h1.Should().BeTrue();
        h2.Should().BeTrue();
        h3.Should().BeTrue();
    }

    [Fact]
    public async Task Dispatch_HandlerFailure_ShouldNotBlockOthers()
    {
        var evt = new DispatcherTestEvent("partial-fail");
        bool h1Called = false, h3Called = false;

        var dispatcher = CreateDispatcher<DispatcherTestEvent>(
            new TestHandler(_ => h1Called = true),
            new ThrowHandler("boom"),
            new TestHandler(_ => h3Called = true));

        var ex = await Assert.ThrowsAsync<AggregateException>(() => dispatcher.DispatchAsync(evt));

        ex.InnerExceptions.Should().HaveCount(1);
        ex.InnerExceptions[0].Should().BeOfType<InvalidOperationException>();

        h1Called.Should().BeTrue();
        h3Called.Should().BeTrue();
    }

    [Fact]
    public async Task Dispatch_AllHandlersFail_ShouldThrowAggregateException()
    {
        var evt = new DispatcherTestEvent("all-fail");
        var dispatcher = CreateDispatcher<DispatcherTestEvent>(
            new ThrowHandler("fail1"),
            new ThrowHandler("fail2"));

        var ex = await Assert.ThrowsAsync<AggregateException>(() => dispatcher.DispatchAsync(evt));
        ex.InnerExceptions.Should().HaveCount(2);
        ex.GetBaseException().Message.Should().Contain("fail1");
    }

    [Fact]
    public async Task Dispatch_Null_ShouldThrow()
    {
        var dispatcher = CreateDispatcher<DispatcherTestEvent>();
        await Assert.ThrowsAsync<ArgumentNullException>(() => dispatcher.DispatchAsync(null!));
    }

    private sealed class TestHandler(Action<DispatcherTestEvent> action) : IDomainEventHandler<DispatcherTestEvent>
    {
        public Task HandleAsync(DispatcherTestEvent @event, CancellationToken cancellationToken)
        {
            action(@event);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowHandler(string message) : IDomainEventHandler<DispatcherTestEvent>
    {
        public Task HandleAsync(DispatcherTestEvent @event, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);
    }

    private sealed record DispatcherTestEvent(string Data) : IDomainEvent;
}
