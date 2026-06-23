using System.Reflection;
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

    #region 8. SendAsync_ConcurrentToSameHandler_AllComplete

    [Fact]
    public async Task SendAsync_ConcurrentToSameHandler_AllComplete()
    {
        var handler = new ThreadSafeCountingHandler();
        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<TestCmd, string>>(handler);
        var dispatcher = new CommandDispatcher(services.BuildServiceProvider());

        const int total = 10;
        var tasks = Enumerable.Range(0, total)
            .Select(i => dispatcher.SendAsync(new TestCmd($"msg-{i}")))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Should().HaveCount(total);
        results.Should().AllSatisfy(r => r.Should().NotBeNullOrEmpty());
        results.Should().Contain(new[] { "MSG-0", "MSG-1", "MSG-2", "MSG-3", "MSG-4", "MSG-5", "MSG-6", "MSG-7", "MSG-8", "MSG-9" });
        handler.CallCount.Should().Be(total);
    }

    // Thread-safe handler for concurrent dispatch tests — uses Interlocked for the counter
    private sealed class ThreadSafeCountingHandler : ICommandHandler<TestCmd, string>
    {
        private int _callCount;
        public int CallCount => _callCount;

        public Task<string> HandleAsync(TestCmd command, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(command.Value.ToUpperInvariant());
        }
    }

    #endregion

    #region 9. SendAsync_ConcurrentToDifferentHandlers_AllComplete

    [Fact]
    public async Task SendAsync_ConcurrentToDifferentHandlers_AllComplete()
    {
        var handlerA = new HandlerA();
        var handlerB = new HandlerB();
        var handlerUnit = new HandlerUnit();
        var handlerC = new HandlerC();
        var handlerD = new HandlerD();

        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<TestCmd, string>>(handlerA);
        services.AddSingleton<ICommandHandler<TestCmdB, string>>(handlerB);
        services.AddSingleton<ICommandHandler<TestCmdUnit, Unit>>(handlerUnit);
        services.AddSingleton<ICommandHandler<TestCmdC, int>>(handlerC);
        services.AddSingleton<ICommandHandler<TestCmdD, bool>>(handlerD);

        var dispatcher = new CommandDispatcher(services.BuildServiceProvider());

        var taskA = dispatcher.SendAsync(new TestCmd("a"));
        var taskB = dispatcher.SendAsync(new TestCmdB("b"));
        var taskU = dispatcher.SendAsync(new TestCmdUnit("u"));
        var taskC = dispatcher.SendAsync(new TestCmdC(21));
        var taskD = dispatcher.SendAsync(new TestCmdD(false));

        // Await each in turn to avoid blocking .Result (xUnit1031) and still verify ordering
        var resultA = await taskA;
        var resultB = await taskB;
        var resultU = await taskU;
        var resultC = await taskC;
        var resultD = await taskD;

        resultA.Should().Be("A");
        resultB.Should().Be("B:b");
        resultU.Should().Be(Unit.Value);
        resultC.Should().Be(42);
        resultD.Should().BeTrue();

        handlerA.CallCount.Should().Be(1);
        handlerUnit.Called.Should().BeTrue();
        handlerC.CallCount.Should().Be(1);
        handlerD.CallCount.Should().Be(1);
    }

    internal sealed record TestCmdC(int Value) : ICommand<int>;
    internal sealed record TestCmdD(bool Value) : ICommand<bool>;

    internal sealed class HandlerC : ICommandHandler<TestCmdC, int>
    {
        public int CallCount { get; private set; }
        public Task<int> HandleAsync(TestCmdC command, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(command.Value * 2);
        }
    }

    internal sealed class HandlerD : ICommandHandler<TestCmdD, bool>
    {
        public int CallCount { get; private set; }
        public Task<bool> HandleAsync(TestCmdD command, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(!command.Value);
        }
    }

    #endregion

    #region 10. SendAsync_HandlerNotRegistered_ThrowsWithDescriptiveMessage

    [Fact]
    public async Task SendAsync_HandlerNotRegistered_ThrowsWithDescriptiveMessage()
    {
        // No handler registered for TestCmdB
        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<TestCmd, string>>(new HandlerA());
        var dispatcher = new CommandDispatcher(services.BuildServiceProvider());

        Func<Task> act = () => dispatcher.SendAsync(new TestCmdB("orphan"));

        // GetRequiredService 抛出 InvalidOperationException，消息中包含请求的服务类型（包含命令类型作为泛型参数）
        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.WithMessage($"*{typeof(TestCmdB).FullName}*");
    }

    #endregion

    #region 11. SendAsync_BehaviorThrows_PropagatesException

    [Fact]
    public async Task SendAsync_BehaviorThrows_PropagatesException()
    {
        var handler = new HandlerA();
        var throwingBehavior = new ThrowingBehavior<TestCmd, string>(
            new InvalidOperationException("behavior boom"));

        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<TestCmd, string>>(handler);
        services.AddSingleton<IPipelineBehavior<TestCmd, string>>(throwingBehavior);
        var dispatcher = new CommandDispatcher(services.BuildServiceProvider());

        Func<Task> act = () => dispatcher.SendAsync(new TestCmd("hello"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*behavior boom*");
        handler.CallCount.Should().Be(0);
    }

    // Pipeline behavior that throws synchronously — used to verify exception propagation
    private sealed class ThrowingBehavior<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
        where TCommand : ICommand<TResult>
    {
        private readonly Exception _exception;
        public ThrowingBehavior(Exception exception) => _exception = exception;

        public Task<TResult> HandleAsync(TCommand command, CommandHandlerDelegate<TResult> next, CancellationToken cancellationToken) =>
            throw _exception;
    }

    #endregion

    #region 12. SendAsync_WrapperCache_ReusedAcrossCalls

    [Fact]
    public async Task SendAsync_WrapperCache_ReusedAcrossCalls()
    {
        var handler = new HandlerA();
        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<TestCmd, string>>(handler);
        var dispatcher = new CommandDispatcher(services.BuildServiceProvider());

        // Start with a clean slate for this command type so we observe only this test's behavior
        CommandDispatcher.WrapperCache.TryRemove(typeof(TestCmd), out _);

        _ = await dispatcher.SendAsync(new TestCmd("first"));
        var wrapperAfterFirst = CommandDispatcher.WrapperCache[typeof(TestCmd)];

        _ = await dispatcher.SendAsync(new TestCmd("second"));
        var wrapperAfterSecond = CommandDispatcher.WrapperCache[typeof(TestCmd)];

        _ = await dispatcher.SendAsync(new TestCmd("third"));
        var wrapperAfterThird = CommandDispatcher.WrapperCache[typeof(TestCmd)];

        // The same wrapper instance should be returned from the cache for every subsequent call
        wrapperAfterFirst.Should().BeSameAs(wrapperAfterSecond);
        wrapperAfterFirst.Should().BeSameAs(wrapperAfterThird);
        handler.CallCount.Should().Be(3);
    }

    #endregion

    #region 13. WrapperCache_IsInternalStatic_ForTestVisibility

    // Issue #10 acceptance: WrapperCache should be internal (not private) so tests can
    // verify cache behavior without reflection. This test guards that contract.
    [Fact]
    public void WrapperCache_IsInternalStatic_ForTestVisibility()
    {
        // Locate the field. InternalsVisibleTo("10E0.Core.Tests") lets us see the field
        // without reflection, but we still go through GetField to assert the *contract*
        // (visibility) is what we promise the test suite.
        var cacheField = typeof(CommandDispatcher).GetField(
            "WrapperCache",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        cacheField.Should().NotBeNull("WrapperCache field must exist for the dispatcher to cache wrappers");
        cacheField!.IsStatic.Should().BeTrue("the cache is process-wide");
        cacheField.IsAssembly.Should().BeTrue(
            "WrapperCache must be internal (assembly-visible) so tests in 10E0.Core.Tests can read it without reflection");
    }

    #endregion

    #region 14. SendAsync_100Iterations_StableWrapperCache

    // Issue #10 acceptance: 100 consecutive runs of the dispatcher must remain stable.
    // Validates that the static cache never leaks wrong wrapper instances and the
    // dispatcher is deterministic across many invocations.
    [Fact]
    public async Task SendAsync_100Iterations_StableWrapperCache()
    {
        var handler = new HandlerA();
        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<TestCmd, string>>(handler);
        var dispatcher = new CommandDispatcher(services.BuildServiceProvider());

        CommandDispatcher.WrapperCache.TryRemove(typeof(TestCmd), out _);

        object? firstWrapper = null;
        for (var i = 0; i < 100; i++)
        {
            _ = await dispatcher.SendAsync(new TestCmd($"iter-{i}"));
            var currentWrapper = CommandDispatcher.WrapperCache[typeof(TestCmd)];
            firstWrapper ??= currentWrapper;
            currentWrapper.Should().BeSameAs(firstWrapper,
                $"iteration {i} must reuse the same wrapper instance from the static cache");
        }

        handler.CallCount.Should().Be(100);
    }

    #endregion

    #region #105: BehaviorPipeline 数组索引推进替代链式闭包 — 多层嵌套顺序

    /// <summary>
    /// #105 回归守护：BehaviorPipeline 用数组索引推进替代链式闭包后，必须验证多层嵌套
    /// （5 个 behavior）的进入/退出顺序仍正确。数组索引推进易在边界搞反方向（逆序变正序），
    /// 本测试用 5 层嵌套强化覆盖。
    ///
    /// 预期：Order 大的先进入最后退出（外→内→外），与 ASP.NET Core 中间件洋葱模型一致。
    /// </summary>
    [Fact]
    public async Task SendAsync_FiveBehaviors_OrderedNestingCorrect()
    {
        var trace = new List<string>();
        var handler = new HandlerA();

        // Order 500 > 400 > 300 > 200 > 100，500 最外层
        var behaviors = new[]
        {
            new OrderTracingBehavior<TestCmd, string>(500, "B500", trace),
            new OrderTracingBehavior<TestCmd, string>(400, "B400", trace),
            new OrderTracingBehavior<TestCmd, string>(300, "B300", trace),
            new OrderTracingBehavior<TestCmd, string>(200, "B200", trace),
            new OrderTracingBehavior<TestCmd, string>(100, "B100", trace),
        };

        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<TestCmd, string>>(handler);
        foreach (var b in behaviors)
            services.AddSingleton<IPipelineBehavior<TestCmd, string>>(b);

        var dispatcher = new CommandDispatcher(services.BuildServiceProvider());
        _ = await dispatcher.SendAsync(new TestCmd("deep"));

        trace.Should().Equal(
            ["B500-enter", "B400-enter", "B300-enter", "B200-enter", "B100-enter",
             "B100-exit", "B200-exit", "B300-exit", "B400-exit", "B500-exit"],
            "5 层 behavior 必须严格按 Order 降序进入、升序退出（洋葱模型）");
    }

    // Order-configurable tracing behavior for #105 nesting test
    private sealed class OrderTracingBehavior<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
        where TCommand : ICommand<TResult>
    {
        private readonly int _order;
        private readonly string _tag;
        private readonly List<string> _trace;
        public int Order => _order;

        public OrderTracingBehavior(int order, string tag, List<string> trace)
        {
            _order = order; _tag = tag; _trace = trace;
        }

        public async Task<TResult> HandleAsync(TCommand command, CommandHandlerDelegate<TResult> next, CancellationToken cancellationToken)
        {
            _trace.Add($"{_tag}-enter");
            try { return await next(cancellationToken); }
            finally { _trace.Add($"{_tag}-exit"); }
        }
    }

    #endregion
}
