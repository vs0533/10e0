using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenE0.Core.Events;
using TenE0.Core.Workflow.StateMachine;
using Sm = TenE0.Core.Workflow.StateMachine.StateMachine;

namespace TenE0.Core.Tests.Workflow.StateMachine;

/// <summary>
/// #157 状态机核心测试。覆盖：合法/非法转换、Guard、FromAny、事件、freeze、Registry。
/// </summary>
[Trait("Category", "Unit")]
public sealed class StateMachineTests
{
    // 演示用 enum 状态 / 动作（public 供 [Theory] 参数化使用）
    public enum OrderState { Draft, Submitted, Approved, Rejected, Cancelled, Completed }
    public enum OrderAction { Submit, Approve, Reject, Cancel, Complete }

    // 简单实体（演示 Guard 求值）
    private sealed class Order
    {
        public OrderState State { get; set; }
        public List<string> Items { get; set; } = [];
        public decimal Amount { get; set; }
    }

    private static StateMachine<OrderState, OrderAction> BuildOrderSm(IDomainEventDispatcher? dispatcher = null)
    {
        var def = Sm.Create<OrderState, OrderAction>(OrderState.Draft)
            .On(OrderAction.Submit).Transit(OrderState.Draft).To(OrderState.Submitted)
                .Guard<Order>(o => o.Items.Count > 0, "ORDER_NO_ITEMS")
                .And()
            .On(OrderAction.Approve).Transit(OrderState.Submitted).To(OrderState.Approved)
                .And()
            .On(OrderAction.Reject).Transit(OrderState.Submitted).To(OrderState.Rejected)
                .And()
            .On(OrderAction.Cancel).FromAny().To(OrderState.Cancelled)
                .Guard<Order>(o => o.State != OrderState.Completed, "ORDER_ALREADY_COMPLETED")
                .And()
            .On(OrderAction.Complete).Transit(OrderState.Approved).To(OrderState.Completed)
                .And()
            .Build();

        return dispatcher is null ? Sm.Create(def) : Sm.Create(def, dispatcher);
    }

    // ================================================================
    // 合法 / 非法转换
    // ================================================================

    [Fact]
    public async Task FireAsync_LegalActionTransition_ReturnsNewState()
    {
        var sm = BuildOrderSm();
        var order = new Order { State = OrderState.Draft, Items = ["x"] };

        var (newState, transition) = await sm.FireAsync(
            OrderState.Draft, OrderAction.Submit, order, "u001");

        newState.Should().Be(OrderState.Submitted);
        transition.From.Should().Be(OrderState.Draft);
        transition.To.Should().Be(OrderState.Submitted);
        transition.Action.Should().Be(OrderAction.Submit);
        transition.Actor.Should().Be("u001");
    }

    [Fact]
    public async Task FireAsync_IllegalAction_ThrowsInvalidTransition()
    {
        var sm = BuildOrderSm();
        var order = new Order { State = OrderState.Draft };

        // Draft → Approve 未声明
        var act = () => sm.FireAsync(OrderState.Draft, OrderAction.Approve, order, "u001");

        var ex = await act.Should().ThrowAsync<InvalidTransitionException<OrderState, OrderAction>>();
        ex.Which.From.Should().Be(OrderState.Draft);
        ex.Which.Action.Should().Be(OrderAction.Approve);
        ex.Which.HasTo.Should().BeFalse("未解析出目标");
        ex.Which.Message.Should().Contain("Draft").And.Contain("Approve");
    }

    // ================================================================
    // Guard
    // ================================================================

    [Fact]
    public async Task FireAsync_GuardFails_ThrowsGuardFailedWithReasons()
    {
        var sm = BuildOrderSm();
        var order = new Order { State = OrderState.Draft, Items = [] }; // 空 Items

        var act = () => sm.FireAsync(OrderState.Draft, OrderAction.Submit, order, "u001");

        var ex = await act.Should().ThrowAsync<GuardFailedException>();
        ex.Which.Reasons.Should().Contain("ORDER_NO_ITEMS");
    }

    [Fact]
    public async Task FireAsync_MultipleGuards_AllMustPass()
    {
        var def = Sm.Create<OrderState, OrderAction>(OrderState.Draft)
            .On(OrderAction.Submit).Transit(OrderState.Draft).To(OrderState.Submitted)
                .Guard<Order>(o => o.Items.Count > 0, "ORDER_NO_ITEMS")
                .Guard<Order>(o => o.Amount > 0, "ORDER_INVALID_AMOUNT")
                .And()
            .Build();
        var sm = Sm.Create(def)!;

        var order = new Order { State = OrderState.Draft, Items = [], Amount = -1 };

        var ex = await sm.Invoking(s => s.FireAsync(OrderState.Draft, OrderAction.Submit, order, "u"))
            .Should().ThrowAsync<GuardFailedException>();
        // 两个 Guard 都失败 → 两个 reason 都报告
        ex.Which.Reasons.Should().BeEquivalentTo(["ORDER_NO_ITEMS", "ORDER_INVALID_AMOUNT"]);
    }

    [Fact]
    public async Task FireAsync_AsyncGuard_Evaluated()
    {
        var def = Sm.Create<OrderState, OrderAction>(OrderState.Draft)
            .On(OrderAction.Submit).Transit(OrderState.Draft).To(OrderState.Submitted)
                .GuardAsync<Order>((o, ct) => Task.FromResult(o.Items.Count > 0), "ORDER_NO_ITEMS")
                .And()
            .Build();
        var sm = Sm.Create(def)!;

        var order = new Order { State = OrderState.Draft, Items = ["x"] };
        var (newState, _) = await sm.FireAsync(OrderState.Draft, OrderAction.Submit, order, "u");

        newState.Should().Be(OrderState.Submitted);
    }

    // ================================================================
    // FromAny
    // ================================================================

    [Theory]
    [InlineData(OrderState.Draft)]
    [InlineData(OrderState.Submitted)]
    [InlineData(OrderState.Approved)]
    public async Task FireAsync_FromAny_AppliesFromAnyState(OrderState from)
    {
        var sm = BuildOrderSm();
        var order = new Order { State = from };

        var (newState, _) = await sm.FireAsync(from, OrderAction.Cancel, order, "u");

        newState.Should().Be(OrderState.Cancelled);
    }

    /// <summary>
    /// 🟡 review 修复回归测试：FromAny 转换与精确转换（From 是 enum 的 default 值）共存时，
    /// 精确转换应胜出，FromAny 不被静默覆盖。
    ///
    /// 之前 FromAny 用 (default!, action) 混入 ActionTransitions，enum 的 default == Draft，
    /// 若再声明 .Transit(Draft).To(X)，两者键冲突 → 后注册者覆盖。
    /// </summary>
    [Fact]
    public async Task FireAsync_ExactTransitionWinsOverFromAny_WhenBothRegistered()
    {
        // Cancel 既 FromAny→Cancelled，又在 Draft 上精确声明→Approved
        // （语义不真实，仅用于验证精确优先 + 不互相覆盖）
        var def = Sm.Create<OrderState, OrderAction>(OrderState.Draft)
            .On(OrderAction.Cancel).FromAny().To(OrderState.Cancelled).And()
            .On(OrderAction.Cancel).Transit(OrderState.Draft).To(OrderState.Approved).And()
            .Build();
        var sm = Sm.Create(def);

        // Draft 上精确声明胜出 → Approved（而非 FromAny 的 Cancelled）
        var (fromDraft, _) = await sm.FireAsync(OrderState.Draft, OrderAction.Cancel, entity: null, "u");
        fromDraft.Should().Be(OrderState.Approved, "精确转换应优先于 FromAny");

        // 其他状态无精确声明 → 走 FromAny → Cancelled
        var (fromSubmitted, _) = await sm.FireAsync(OrderState.Submitted, OrderAction.Cancel, entity: null, "u");
        fromSubmitted.Should().Be(OrderState.Cancelled);
    }

    /// <summary>FromAny 的 Guard 在精确转换未命中时正确评估。</summary>
    [Fact]
    public async Task FireAsync_FromAnyGuardEvaluated_WhenNoExactTransition()
    {
        var def = Sm.Create<OrderState, OrderAction>(OrderState.Draft)
            .On(OrderAction.Cancel).FromAny().To(OrderState.Cancelled)
                .Guard<Order>(o => o.Items.Count > 0, "EMPTY").And()
            .Build();
        var sm = Sm.Create(def);

        // Guard 失败（空 Items）
        var order = new Order { State = OrderState.Submitted, Items = [] };
        await sm.Invoking(s => s.FireAsync(OrderState.Submitted, OrderAction.Cancel, order, "u"))
            .Should().ThrowAsync<GuardFailedException>()
            .WithMessage("*EMPTY*");
    }

    [Fact]
    public async Task FireAsync_FromAnyGuardBlocksCompleted()
    {
        var sm = BuildOrderSm();
        var order = new Order { State = OrderState.Completed };

        await sm.Invoking(s => s.FireAsync(OrderState.Completed, OrderAction.Cancel, order, "u"))
            .Should().ThrowAsync<GuardFailedException>()
            .WithMessage("*ORDER_ALREADY_COMPLETED*");
    }

    [Fact]
    public void CanFire_ReportsTransitionValidity()
    {
        var sm = BuildOrderSm();

        sm.CanFire(OrderState.Draft, OrderAction.Submit).Should().BeTrue();
        sm.CanFire(OrderState.Draft, OrderAction.Approve).Should().BeFalse();
        // FromAny 的 Cancel 在所有状态都 true
        sm.CanFire(OrderState.Approved, OrderAction.Cancel).Should().BeTrue();
    }

    // ================================================================
    // 事件触发
    // ================================================================

    private sealed class CapturingDispatcher : IDomainEventDispatcher
    {
        public List<IDomainEvent> Dispatched { get; } = [];
        public Task DispatchAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
        {
            Dispatched.Add(domainEvent);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task FireAsync_WithDispatcher_TriggersThreeEventsInOrder()
    {
        var dispatcher = new CapturingDispatcher();
        var sm = BuildOrderSm(dispatcher);
        var order = new Order { State = OrderState.Draft, Items = ["x"] };

        await sm.FireAsync(OrderState.Draft, OrderAction.Submit, order, "u001");

        // Exited → Transition → Entered
        dispatcher.Dispatched.Should().HaveCount(3);
        dispatcher.Dispatched[0].Should().BeOfType<StateExitedEvent<object>>();
        dispatcher.Dispatched[1].Should().BeOfType<StateTransitionEvent<object, OrderState, OrderAction>>();
        dispatcher.Dispatched[2].Should().BeOfType<StateEnteredEvent<object>>();
    }

    [Fact]
    public async Task FireAsync_NullDispatcher_NoEventsButTransitionStillReturned()
    {
        var sm = BuildOrderSm(dispatcher: null);
        var order = new Order { State = OrderState.Draft, Items = ["x"] };

        var (newState, transition) = await sm.FireAsync(OrderState.Draft, OrderAction.Submit, order, "u");

        newState.Should().Be(OrderState.Submitted);
        transition.To.Should().Be(OrderState.Submitted);
    }

    [Fact]
    public async Task FireAsync_NullEntity_GuardSkippedButTransitionProceeds()
    {
        var sm = BuildOrderSm();
        // 无 entity → Guard 无法求值，跳过（不阻止）。适用于"纯状态切换无需 Guard"的场景。
        var (newState, _) = await sm.FireAsync(OrderState.Submitted, OrderAction.Approve, entity: null, "u");
        newState.Should().Be(OrderState.Approved);
    }

    // ================================================================
    // Freeze
    // ================================================================

    [Fact]
    public void Definition_NotFrozen_ThrowsOnEngineCreate()
    {
        var def = new StateMachineDefinition<OrderState, OrderAction>
        {
            InitialState = OrderState.Draft
        };

        var act = () => Sm.Create(def);

        act.Should().Throw<InvalidOperationException>("未 freeze 的定义不能用于引擎");
    }

    [Fact]
    public void Builder_Build_FreezesDefinition()
    {
        var def = Sm.Create<OrderState, OrderAction>(OrderState.Draft)
            .On(OrderAction.Submit).Transit(OrderState.Draft).To(OrderState.Submitted)
            .And()
            .Build();

        def.IsFrozen.Should().BeTrue();
    }

    [Fact]
    public void Builder_MissingInitialState_ThrowsOnFreeze()
    {
        var def = new StateMachineDefinition<OrderState, OrderAction>();

        var act = () => def.Freeze();

        act.Should().Throw<InvalidOperationException>("必须设置 InitialState");
    }

    // ================================================================
    // Registry
    // ================================================================

    private sealed class OrderStateMachineDefinition : StateMachineDefinitionBase<OrderState, OrderAction>
    {
        public override StateMachineDefinition<OrderState, OrderAction> Define()
            => Sm.Create<OrderState, OrderAction>(OrderState.Draft)
                .On(OrderAction.Submit).Transit(OrderState.Draft).To(OrderState.Submitted)
                .And()
                .Build();
    }

    [Fact]
    public void Registry_Get_ReturnsEngineForRegisteredDefinition()
    {
        var services = new ServiceCollection();
        services.AddScoped<OrderStateMachineDefinition>();
        services.Add(ServiceDescriptor.Scoped(typeof(IStateMachineDefinition), sp =>
            sp.GetRequiredService<OrderStateMachineDefinition>()));
        services.AddScoped<IStateMachineRegistry, StateMachineRegistry>();
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IStateMachineRegistry>();

        registry.IsRegistered<OrderState, OrderAction>().Should().BeTrue();
        var sm = registry.Get<OrderState, OrderAction>();
        sm.Should().NotBeNull();
    }

    [Fact]
    public void Registry_Get_Unregistered_Throws()
    {
        var services = new ServiceCollection();
        services.AddScoped<IStateMachineRegistry, StateMachineRegistry>();
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IStateMachineRegistry>();

        registry.Invoking(r => r.Get<OrderState, OrderAction>())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*未注册状态机定义*");
    }
}
