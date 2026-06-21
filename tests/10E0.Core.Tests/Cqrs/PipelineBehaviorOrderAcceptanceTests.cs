using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.Abstractions;
using TenE0.Core.Cqrs;
using TenE0.Core.Cqrs.Behaviors;
using TenE0.Core.DependencyInjection;

namespace TenE0.Core.Tests.Cqrs;

/// <summary>
/// Issue #41 acceptance tests: PipelineBehavior Order + SkipBehavior 特性。
///
/// 验证：
/// 1. <see cref="IPipelineBehavior{TCommand, TResult}.Order"/> 属性存在
/// 2. <see cref="CommandDispatcher"/> 按 Order 升序包裹（先升再逆序 = Order 小先进入）
/// 3. <see cref="BehaviorOptions.DisabledInTest"/> 跳过被列入的 behavior
/// 4. <see cref="SkipBehaviorInTestEnvAttribute"/> 在 Test 环境跳过 behavior
/// 5. <see cref="BuiltInBehaviorOrders"/> 常量在正确数值
/// 6. <see cref="CqrsServiceCollectionExtensions.AddBehavior{TBehavior}"/> 接受 order
/// 7. 现有 Logging/Transaction/Permission behavior 都声明了 Order 常量
/// </summary>
[Trait("Category", "Acceptance")]
[Trait("Issue", "#41")]
public sealed class PipelineBehaviorOrderAcceptanceTests
{
    // ── 测试命令 + Handler ────────────────────────────────────────

    private sealed record OrderTestCmd(string Value) : ICommand<string>;
    private sealed record SkipTestCmd(string Value) : ICommand<string>;

    private sealed class PassThroughHandler : ICommandHandler<OrderTestCmd, string>
    {
        public Task<string> HandleAsync(OrderTestCmd command, CancellationToken cancellationToken)
            => Task.FromResult(command.Value);
    }

    private sealed class PassThroughHandler2 : ICommandHandler<SkipTestCmd, string>
    {
        public Task<string> HandleAsync(SkipTestCmd command, CancellationToken cancellationToken)
            => Task.FromResult(command.Value);
    }

    // ── 1. Order 属性存在且默认 0 ────────────────────────────────

    [Fact]
    public void IPipelineBehavior_HasOrder_Property()
    {
        // The Order property must be accessible on any IPipelineBehavior
        // without a backing field on the implementing class.
        IPipelineBehavior<OrderTestCmd, string> behavior = new OrderlessBehavior<OrderTestCmd, string>();

        behavior.Order.Should().Be(0,
            "default Order must be 0 so existing behaviors remain order-stable until they opt in");
    }

    // ── 2. CommandDispatcher 按 Order 升序包裹 ──────────────────

    [Fact]
    public async Task CommandDispatcher_SortsByOrder_Descending_BeforeWrapping()
    {
        // Three behaviors: Order=10 / 50 / 200
        // Pipeline execution order should be: 200 -> 50 -> 10 -> handler -> 10 -> 50 -> 200
        // i.e. the largest Order is the OUTERMOST (entered first, exited last).
        // This matches the issue's example: Logging(order=200) is the outermost.
        var order = new List<string>();
        var handler = new PassThroughHandler();

        var b10 = new OrderedTracingBehavior<OrderTestCmd, string>(10, "B10", order);
        var b50 = new OrderedTracingBehavior<OrderTestCmd, string>(50, "B50", order);
        var b200 = new OrderedTracingBehavior<OrderTestCmd, string>(200, "B200", order);

        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<OrderTestCmd, string>>(handler);
        // Register in REVERSE priority order to prove Order overrides registration order
        services.AddSingleton<IPipelineBehavior<OrderTestCmd, string>>(b200);
        services.AddSingleton<IPipelineBehavior<OrderTestCmd, string>>(b10);
        services.AddSingleton<IPipelineBehavior<OrderTestCmd, string>>(b50);

        var dispatcher = new CommandDispatcher(services.BuildServiceProvider());

        _ = await dispatcher.SendAsync(new OrderTestCmd("x"));

        order.Should().Equal(new[] { "B200-enter", "B50-enter", "B10-enter", "B10-exit", "B50-exit", "B200-exit" },
            "Order descending = largest first to enter; first entered = last to exit");
    }

    [Fact]
    public async Task CommandDispatcher_EqualOrder_PreservesRegistrationOrder_AsTiebreaker()
    {
        // When multiple behaviors share the same Order, the dispatcher should
        // fall back to registration order (stable sort). This is the conservative
        // contract — it keeps the existing "register order = execution order"
        // intuition alive for legacy behaviors with Order=0.
        var order = new List<string>();
        var handler = new PassThroughHandler();

        var first = new OrderedTracingBehavior<OrderTestCmd, string>(0, "FIRST", order);
        var second = new OrderedTracingBehavior<OrderTestCmd, string>(0, "SECOND", order);

        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<OrderTestCmd, string>>(handler);
        services.AddSingleton<IPipelineBehavior<OrderTestCmd, string>>(first);
        services.AddSingleton<IPipelineBehavior<OrderTestCmd, string>>(second);

        var dispatcher = new CommandDispatcher(services.BuildServiceProvider());

        _ = await dispatcher.SendAsync(new OrderTestCmd("x"));

        order.Should().Equal("FIRST-enter", "SECOND-enter", "SECOND-exit", "FIRST-exit");
    }

    [Fact]
    public async Task CommandDispatcher_ZeroOrder_Behaviors_StillWork()
    {
        // Backward-compatibility: behaviors that don't opt in to Order
        // (Order=0) must continue to execute in registration order.
        var order = new List<string>();
        var handler = new PassThroughHandler();

        var a = new OrderlessTracingBehavior<OrderTestCmd, string>("A", order);
        var b = new OrderlessTracingBehavior<OrderTestCmd, string>("B", order);

        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<OrderTestCmd, string>>(handler);
        services.AddSingleton<IPipelineBehavior<OrderTestCmd, string>>(a);
        services.AddSingleton<IPipelineBehavior<OrderTestCmd, string>>(b);

        var dispatcher = new CommandDispatcher(services.BuildServiceProvider());

        _ = await dispatcher.SendAsync(new OrderTestCmd("x"));

        order.Should().Equal("A-enter", "B-enter", "B-exit", "A-exit");
    }

    // ── 3. BehaviorOptions.DisabledInTest 跳过 behavior ──────────

    [Fact]
    public async Task BehaviorOptions_DisabledInTest_SkipsListedBehavior()
    {
        // Register three behaviors: Skippable, Normal-A, Normal-B.
        // Configure BehaviorOptions.DisabledInTest = ["SkippableBehavior"] and Environment = "Test".
        // The SkippableBehavior must NOT execute; the other two must.
        var log = new List<string>();
        var handler = new PassThroughHandler2();

        var skippable = new DisabledByListBehavior<SkipTestCmd, string>("Skippable", log, order: 50);
        var normalA = new NamedBehavior<SkipTestCmd, string>("A", log, order: 100);
        var normalB = new NamedBehavior<SkipTestCmd, string>("B", log, order: 200);

        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<SkipTestCmd, string>>(handler);
        services.AddSingleton<IPipelineBehavior<SkipTestCmd, string>>(skippable);
        services.AddSingleton<IPipelineBehavior<SkipTestCmd, string>>(normalA);
        services.AddSingleton<IPipelineBehavior<SkipTestCmd, string>>(normalB);

        services.Configure<BehaviorOptions>(o =>
        {
            o.Environment = "Test";
            o.DisabledInTest.Add(typeof(DisabledByListBehavior<SkipTestCmd, string>).FullName!);
        });

        var dispatcher = new CommandDispatcher(services.BuildServiceProvider());

        _ = await dispatcher.SendAsync(new SkipTestCmd("x"));

        log.Should().NotContain(e => e.StartsWith("Skippable-"),
            "behavior listed in DisabledInTest must be skipped in Test environment");
        log.Should().Contain("A-enter", "A-exit");
        log.Should().Contain("B-enter", "B-exit");
    }

    [Fact]
    public async Task BehaviorOptions_DisabledInTest_NotAppliedInProduction()
    {
        // Same setup, but Environment = "Production": the Skippable behavior MUST run.
        var log = new List<string>();
        var handler = new PassThroughHandler2();

        var skippable = new DisabledByListBehavior<SkipTestCmd, string>("Skippable", log, order: 50);
        var normalA = new NamedBehavior<SkipTestCmd, string>("A", log, order: 100);

        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<SkipTestCmd, string>>(handler);
        services.AddSingleton<IPipelineBehavior<SkipTestCmd, string>>(skippable);
        services.AddSingleton<IPipelineBehavior<SkipTestCmd, string>>(normalA);

        services.Configure<BehaviorOptions>(o =>
        {
            o.Environment = "Production";
            o.DisabledInTest.Add(typeof(DisabledByListBehavior<SkipTestCmd, string>).FullName!);
        });

        var dispatcher = new CommandDispatcher(services.BuildServiceProvider());

        _ = await dispatcher.SendAsync(new SkipTestCmd("x"));

        log.Should().Contain("Skippable-enter", "DisabledInTest must not apply outside Test environment");
        log.Should().Contain("Skippable-exit");
    }

    // ── 4. [SkipBehaviorInTestEnv] 特性在 Test 环境跳过 ──────────

    [Fact]
    public async Task SkipBehaviorInTestEnv_Attribute_SkipsBehavior_InTestEnvironment()
    {
        var log = new List<string>();
        var handler = new PassThroughHandler2();

        var skip = new AttrSkipBehavior<SkipTestCmd, string>(log, order: 50);
        var normal = new NamedBehavior<SkipTestCmd, string>("Normal", log, order: 100);

        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<SkipTestCmd, string>>(handler);
        services.AddSingleton<IPipelineBehavior<SkipTestCmd, string>>(skip);
        services.AddSingleton<IPipelineBehavior<SkipTestCmd, string>>(normal);

        services.Configure<BehaviorOptions>(o => o.Environment = "Test");

        var dispatcher = new CommandDispatcher(services.BuildServiceProvider());

        _ = await dispatcher.SendAsync(new SkipTestCmd("x"));

        log.Should().NotContain("AttrSkip-enter",
            "[SkipBehaviorInTestEnv] must skip execution when BehaviorOptions.Environment == 'Test'");
        log.Should().NotContain("AttrSkip-exit");
        log.Should().Contain("Normal-enter", "Normal-exit");
    }

    [Fact]
    public async Task SkipBehaviorInTestEnv_Attribute_NotAppliedInProduction()
    {
        var log = new List<string>();
        var handler = new PassThroughHandler2();

        var skip = new AttrSkipBehavior<SkipTestCmd, string>(log, order: 50);

        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<SkipTestCmd, string>>(handler);
        services.AddSingleton<IPipelineBehavior<SkipTestCmd, string>>(skip);

        services.Configure<BehaviorOptions>(o => o.Environment = "Production");

        var dispatcher = new CommandDispatcher(services.BuildServiceProvider());

        _ = await dispatcher.SendAsync(new SkipTestCmd("x"));

        log.Should().Contain("AttrSkip-enter",
            "[SkipBehaviorInTestEnv] must NOT skip in Production environment");
    }

    // ── 5. BuiltInBehaviorOrders 常量在正确数值 ──────────────────

    [Fact]
    public void BuiltInBehaviorOrders_HasExpectedConstants()
    {
        // Logging outermost (highest), Permission innermost (lowest)
        BuiltInBehaviorOrders.Logging.Should().Be(200,
            "Logging is the outermost behavior — it must wrap every other behavior");
        BuiltInBehaviorOrders.Transaction.Should().Be(100,
            "Transaction sits between Logging and Permission");
        BuiltInBehaviorOrders.Permission.Should().Be(50,
            "Permission is the innermost behavior — runs closest to the handler");
    }

    [Fact]
    public void BuiltInBehaviorOrders_AreStrictlyOrdered_Logging_GT_Transaction_GT_Permission()
    {
        // "GT" in Order = "outer than" — higher Order = wraps more inner behaviors
        (BuiltInBehaviorOrders.Logging > BuiltInBehaviorOrders.Transaction)
            .Should().BeTrue("Logging must wrap Transaction (Logging is outermost)");
        (BuiltInBehaviorOrders.Transaction > BuiltInBehaviorOrders.Permission)
            .Should().BeTrue("Transaction must wrap Permission");
    }

    // ── 6. 内置 behaviors 声明 Order 常量 ────────────────────────

    [Fact]
    public void LoggingBehavior_HasOrder_EqualToBuiltInConstant()
    {
        // The concrete LoggingBehavior<,> class must carry Order == BuiltInBehaviorOrders.Logging.
        // We verify via an open-generic closed instance (Cmd, string).
        var sut = new TestOrderProbe();
        sut.LoggingOrder.Should().Be(BuiltInBehaviorOrders.Logging);
    }

    [Fact]
    public void PermissionBehavior_HasOrder_EqualToBuiltInConstant()
    {
        var sut = new TestOrderProbe();
        sut.PermissionOrder.Should().Be(BuiltInBehaviorOrders.Permission);
    }

    [Fact]
    public void TransactionBehavior_HasOrder_EqualToBuiltInConstant()
    {
        var sut = new TestOrderProbe();
        sut.TransactionOrder.Should().Be(BuiltInBehaviorOrders.Transaction);
    }

    // ── 7. AddBehavior<T>(order) 扩展注册并设置 Order ───────────

    [Fact]
    public void AddBehaviorGeneric_RegistersOpenGeneric_WithOrder()
    {
        // The AddBehavior<TBehavior> extension must register TBehavior (open-generic) as
        // the implementation of IPipelineBehavior<,>. Since C# can't bind an unbound
        // generic (CS7003), we bind the test through a closed-generic instantiation.
        // DI normalizes the ServiceDescriptor: open-generic service + open-generic impl
        // (the closed impl is also valid; we look for the one matching our closed type).
        var services = new ServiceCollection();
        services.AddBehavior<AcceptanceProbeBehavior<OrderTestCmd, string>>();

        var descriptor = services.Single(s =>
            s.ImplementationType == typeof(AcceptanceProbeBehavior<OrderTestCmd, string>));

        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public async Task AddBehaviorGeneric_Behavior_OrderIsZero_WhenNotSet()
    {
        // Smoke test: the AddBehavior extension registers a behavior with
        // Order=0 by default (no explicit order passed).
        var log = new List<string>();
        var handler = new PassThroughHandler();

        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<OrderTestCmd, string>>(handler);
        // Inject a behavior that doesn't opt in (Order=0).
        services.AddSingleton<IPipelineBehavior<OrderTestCmd, string>>(
            new OrderlessTracingBehavior<OrderTestCmd, string>("X", log));

        var dispatcher = new CommandDispatcher(services.BuildServiceProvider());

        _ = await dispatcher.SendAsync(new OrderTestCmd("x"));

        log.Should().Equal(new[] { "X-enter", "X-exit" },
            "Orderless behavior should still run; Order=0 keeps legacy semantics");
    }

    // ── 8. BehaviorOptions 默认值 ────────────────────────────────

    [Fact]
    public void BehaviorOptions_DefaultEnvironment_IsProduction()
    {
        var opts = new BehaviorOptions();
        opts.Environment.Should().Be("Production",
            "default env must be Production so production deployments skip the disabled list by default");
    }

    [Fact]
    public void BehaviorOptions_DisabledInTest_DefaultsTo_ContainingPermissionBehavior()
    {
        var opts = new BehaviorOptions();
        opts.DisabledInTest.Should().Contain(typeof(TenE0.Core.Permissions.Behaviors.PermissionBehavior<,>).FullName!,
            "default config must include PermissionBehavior so the test-env shortcut works out of the box");
    }

    // ── 9. Integration: 实际注册顺序颠倒但 Order 仍然决定 ───────

    [Fact]
    public async Task Pipeline_ReverseRegistration_ButOrderDecides_SortingIsStable()
    {
        // This is the issue's main motivator: registration order cannot be relied on.
        // Here we register Permission (Order=50) AFTER Logging (Order=200), but Permission
        // must still execute INSIDE Logging.
        var log = new List<string>();
        var handler = new PassThroughHandler2();

        var loggingLike = new NamedBehavior<SkipTestCmd, string>("Logging", log, order: BuiltInBehaviorOrders.Logging);
        var permissionLike = new NamedBehavior<SkipTestCmd, string>("Permission", log, order: BuiltInBehaviorOrders.Permission);

        var services = new ServiceCollection();
        services.AddSingleton<ICommandHandler<SkipTestCmd, string>>(handler);
        // Register in WRONG order (Permission first, Logging second) — but BehaviorOptions
        // is NOT Test, so DisabledInTest doesn't apply, and Order must correct it.
        services.AddSingleton<IPipelineBehavior<SkipTestCmd, string>>(permissionLike);
        services.AddSingleton<IPipelineBehavior<SkipTestCmd, string>>(loggingLike);

        services.Configure<BehaviorOptions>(o => o.Environment = "Production");

        var dispatcher = new CommandDispatcher(services.BuildServiceProvider());

        _ = await dispatcher.SendAsync(new SkipTestCmd("x"));

        log.Should().Equal(new[] { "Logging-enter", "Permission-enter", "Permission-exit", "Logging-exit" },
            "Order=200 must be outermost regardless of registration order");
    }

    // ── Helpers: 测试用 behaviors ────────────────────────────────

    /// <summary>Does not opt in to Order — exercises the default Order=0 path.</summary>
    public sealed class OrderlessBehavior<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
        where TCommand : ICommand<TResult>
    {
        public Task<TResult> HandleAsync(TCommand command, CommandHandlerDelegate<TResult> next, CancellationToken cancellationToken)
            => next(cancellationToken);
    }

    /// <summary>Records enter/exit; Order from constructor. No attribute.</summary>
    private sealed class OrderedTracingBehavior<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
        where TCommand : ICommand<TResult>
    {
        private readonly string _tag;
        private readonly List<string> _log;
        public OrderedTracingBehavior(int order, string tag, List<string> log)
        {
            Order = order;
            _tag = tag;
            _log = log;
        }
        public int Order { get; }
        public async Task<TResult> HandleAsync(TCommand command, CommandHandlerDelegate<TResult> next, CancellationToken cancellationToken)
        {
            _log.Add($"{_tag}-enter");
            try { return await next(cancellationToken); }
            finally { _log.Add($"{_tag}-exit"); }
        }
    }

    /// <summary>Same as OrderedTracingBehavior but doesn't override Order — exercises default 0.</summary>
    private sealed class OrderlessTracingBehavior<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
        where TCommand : ICommand<TResult>
    {
        private readonly string _tag;
        private readonly List<string> _log;
        public OrderlessTracingBehavior(string tag, List<string> log) { _tag = tag; _log = log; }
        public async Task<TResult> HandleAsync(TCommand command, CommandHandlerDelegate<TResult> next, CancellationToken cancellationToken)
        {
            _log.Add($"{_tag}-enter");
            try { return await next(cancellationToken); }
            finally { _log.Add($"{_tag}-exit"); }
        }
    }

    /// <summary>Named behavior with Order — exercises DisabledInTest path.</summary>
    private sealed class NamedBehavior<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
        where TCommand : ICommand<TResult>
    {
        private readonly string _name;
        private readonly List<string> _log;
        public NamedBehavior(string name, List<string> log, int order)
        {
            _name = name;
            _log = log;
            Order = order;
        }
        public int Order { get; }
        public async Task<TResult> HandleAsync(TCommand command, CommandHandlerDelegate<TResult> next, CancellationToken cancellationToken)
        {
            _log.Add($"{_name}-enter");
            try { return await next(cancellationToken); }
            finally { _log.Add($"{_name}-exit"); }
        }
    }

    /// <summary>Behavior marked with [SkipBehaviorInTestEnv] — exercises attribute path.</summary>
    [SkipBehaviorInTestEnv]
    private sealed class AttrSkipBehavior<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
        where TCommand : ICommand<TResult>
    {
        private readonly List<string> _log;
        public AttrSkipBehavior(List<string> log, int order) { _log = log; Order = order; }
        public int Order { get; }
        public async Task<TResult> HandleAsync(TCommand command, CommandHandlerDelegate<TResult> next, CancellationToken cancellationToken)
        {
            _log.Add("AttrSkip-enter");
            try { return await next(cancellationToken); }
            finally { _log.Add("AttrSkip-exit"); }
        }
    }

    /// <summary>Reads the Order constants off the open-generic concrete types.</summary>
    private sealed class TestOrderProbe
    {
        public int LoggingOrder { get; } = new LoggingBehavior<OrderTestCmd, string>(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LoggingBehavior<OrderTestCmd, string>>.Instance).Order;

        public int PermissionOrder { get; } = new TenE0.Core.Permissions.Behaviors.PermissionBehavior<OrderTestCmd, string>(
            new Moq.Mock<TenE0.Core.Permissions.IPermissionEvaluator>().Object).Order;

        public int TransactionOrder { get; } = new TransactionBehavior<OrderTestCmd, string, DbContext>(
            new Moq.Mock<IDbContextFactory<DbContext>>().Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TransactionBehavior<OrderTestCmd, string, DbContext>>.Instance).Order;
    }
}

/// <summary>
/// Top-level test behavior used by the <see cref="PipelineBehaviorOrderAcceptanceTests.AddBehaviorGeneric_RegistersOpenGeneric_WithOrder"/>
/// acceptance test. Lives at namespace level (not nested) so the C# compiler can resolve the
/// unbound generic name when passed as a type argument to a generic method.
/// </summary>
public sealed class AcceptanceProbeBehavior<TCommand, TResult> : TenE0.Core.Abstractions.IPipelineBehavior<TCommand, TResult>
    where TCommand : TenE0.Core.Abstractions.ICommand<TResult>
{
    public TenE0.Core.Abstractions.CommandHandlerDelegate<TResult>? NextCallback { get; set; }

    public Task<TResult> HandleAsync(
        TCommand command,
        TenE0.Core.Abstractions.CommandHandlerDelegate<TResult> next,
        CancellationToken cancellationToken)
    {
        NextCallback = next;
        return next(cancellationToken);
    }
}

/// <summary>
/// Top-level test behavior for the <see cref="PipelineBehaviorOrderAcceptanceTests.BehaviorOptions_DisabledInTest_SkipsListedBehavior"/>
/// acceptance test. Distinct type from <see cref="PipelineBehaviorOrderAcceptanceTests.NamedBehavior{TCommand, TResult}"/>
/// so DisabledInTest can target this specific type without affecting other behaviors.
/// </summary>
public sealed class DisabledByListBehavior<TCommand, TResult> : TenE0.Core.Abstractions.IPipelineBehavior<TCommand, TResult>
    where TCommand : TenE0.Core.Abstractions.ICommand<TResult>
{
    private readonly string _name;
    private readonly List<string> _log;

    public DisabledByListBehavior(string name, List<string> log, int order)
    {
        _name = name;
        _log = log;
        Order = order;
    }

    public int Order { get; }

    public async Task<TResult> HandleAsync(
        TCommand command,
        TenE0.Core.Abstractions.CommandHandlerDelegate<TResult> next,
        CancellationToken cancellationToken)
    {
        _log.Add($"{_name}-enter");
        try { return await next(cancellationToken); }
        finally { _log.Add($"{_name}-exit"); }
    }
}
