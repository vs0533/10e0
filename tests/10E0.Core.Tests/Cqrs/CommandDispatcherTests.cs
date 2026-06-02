using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.Abstractions;
using TenE0.Core.Cqrs;

namespace TenE0.Core.Tests.Cqrs;

public sealed class CommandDispatcherTests
{
    internal sealed record TestCmd(string Value) : ICommand<string>;
    internal sealed record TestCmdUnit(string Value) : ICommand<Unit>;
    internal sealed record TestCmdB(string Value) : ICommand<string>;

    internal sealed class HandlerA : ICommandHandler<TestCmd, string>
    {
        public int CallCount { get; private set; }
        public Task<string> HandleAsync(TestCmd command, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(command.Value.ToUpperInvariant());
        }
    }

    internal sealed class HandlerUnit : ICommandHandler<TestCmdUnit, Unit>
    {
        public bool Called { get; private set; }
        public Task<Unit> HandleAsync(TestCmdUnit command, CancellationToken cancellationToken)
        {
            Called = true;
            return Unit.Task;
        }
    }

    internal sealed class HandlerB : ICommandHandler<TestCmdB, string>
    {
        public Task<string> HandleAsync(TestCmdB command, CancellationToken cancellationToken) =>
            Task.FromResult($"B:{command.Value}");
    }

    #region 1. SendAsync_NullCommand_ShouldThrow

    [Fact]
    public async Task SendAsync_NullCommand_ShouldThrow()
    {
        var dispatcher = new CommandDispatcher(new ServiceCollection().BuildServiceProvider());

        await Assert.ThrowsAsync<ArgumentNullException>(() => dispatcher.SendAsync<string>(null!));
    }

    #endregion

    #region 2. SendAsync_NoHandler_Registered_ShouldThrow

    [Fact]
    public async Task SendAsync_NoHandlerRegistered_ShouldThrow()
    {
        var dispatcher = new CommandDispatcher(new ServiceCollection().BuildServiceProvider());
        var act = () => dispatcher.SendAsync(new TestCmd("x"));

        // GetRequiredService 抛出 InvalidOperationException 当服务未注册
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    #endregion

    #region 3. SendAsync_WithHandler_ShouldExecute

    [Fact]
    public async Task SendAsync_WithHandler_ShouldExecute()
    {
        var handler = new HandlerA();
        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<TestCmd, string>>(handler);
        var dispatcher = new CommandDispatcher(services.BuildServiceProvider());

        var result = await dispatcher.SendAsync(new TestCmd("hello"));

        result.Should().Be("HELLO");
        handler.CallCount.Should().Be(1);
    }

    #endregion

    #region 4. SendAsync_WithUnit_ReturnsUnit

    [Fact]
    public async Task SendAsync_WithUnit_ReturnsUnit()
    {
        var handler = new HandlerUnit();
        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<TestCmdUnit, Unit>>(handler);
        var dispatcher = new CommandDispatcher(services.BuildServiceProvider());

        var result = await dispatcher.SendAsync(new TestCmdUnit("doit"));

        result.Should().Be(Unit.Value);
        handler.Called.Should().BeTrue();
    }

    #endregion

    #region 5. SendAsync_WithBehaviors_ShouldWrapInReverseOrder

    [Fact]
    public async Task SendAsync_WithBehaviors_ShouldWrapInReverseOrder()
    {
        var order = new List<string>();
        var handler = new HandlerA();

        var behavior1 = new TracingBehavior<TestCmd, string>("B1", (tag) => order.Add($"{tag}-enter"), (tag) => order.Add($"{tag}-exit"));
        var behavior2 = new TracingBehavior<TestCmd, string>("B2", (tag) => order.Add($"{tag}-enter"), (tag) => order.Add($"{tag}-exit"));

        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<TestCmd, string>>(handler);
        services.AddSingleton<IPipelineBehavior<TestCmd, string>>(behavior1);
        services.AddSingleton<IPipelineBehavior<TestCmd, string>>(behavior2);

        var dispatcher = new CommandDispatcher(services.BuildServiceProvider());

        _ = await dispatcher.SendAsync(new TestCmd("order"));

        order.Should().Equal("B1-enter", "B2-enter", "B2-exit", "B1-exit");
    }

    // Concrete behavior for testing pipeline order — records enter/exit via callbacks
    private sealed class TracingBehavior<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
        where TCommand : ICommand<TResult>
    {
        private readonly string _tag;
        private readonly Action<string> _onEnter;
        private readonly Action<string> _onExit;

        public TracingBehavior(string tag, Action<string> onEnter, Action<string> onExit)
        {
            _tag = tag; _onEnter = onEnter; _onExit = onExit;
        }

        public async Task<TResult> HandleAsync(TCommand command, CommandHandlerDelegate<TResult> next, CancellationToken cancellationToken)
        {
            _onEnter(_tag);
            try
            {
                return await next(cancellationToken);
            }
            finally
            {
                _onExit(_tag);
            }
        }
    }

    #endregion

    #region 6. SendAsync_WithMultipleCommands_ShouldRouteCorrectly

    [Fact]
    public async Task SendAsync_WithMultipleCommands_ShouldRouteCorrectly()
    {
        var handlerA = new HandlerA();
        var handlerB = new HandlerB();

        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<TestCmd, string>>(handlerA);
        services.AddSingleton<ICommandHandler<TestCmdB, string>>(handlerB);

        var dispatcher = new CommandDispatcher(services.BuildServiceProvider());

        var resultA = await dispatcher.SendAsync(new TestCmd("a"));
        var resultB = await dispatcher.SendAsync(new TestCmdB("b"));

        resultA.Should().Be("A");
        resultB.Should().Be("B:b");
        handlerA.CallCount.Should().Be(1);
    }

    #endregion

    #region 7. SendAsync_CachedWrapper_ShouldReuse

    [Fact]
    public async Task SendAsync_CachedWrapper_ShouldReuse()
    {
        var sharedHandler = new HandlerA();

        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<TestCmd, string>>(sharedHandler);

        var dispatcher = new CommandDispatcher(services.BuildServiceProvider());

        _ = await dispatcher.SendAsync(new TestCmd("first"));
        _ = await dispatcher.SendAsync(new TestCmd("second"));

        sharedHandler.CallCount.Should().Be(2);
    }

    #endregion
}
