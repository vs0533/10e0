using System.Collections.Frozen;

namespace TenE0.Core.Workflow.StateMachine;

/// <summary>
/// 状态机 Fluent API 构造器。
///
/// 用法：
/// <code>
/// var sm = StateMachine&lt;OrderState, OrderAction&gt;.Create(OrderState.Draft)
///     .On(OrderAction.Submit).Transit(OrderState.Draft).To(OrderState.Submitted)
///         .Guard&lt;Order&gt;(o =&gt; o.Items.Count &gt; 0, "ORDER_NO_ITEMS")
///     .On(OrderAction.Cancel).FromAny().To(OrderState.Cancelled)
///     .Build();
/// </code>
///
/// 转换有两种语义：
/// <list type="bullet">
/// <item><b>动作驱动</b>（最常用）：<c>.On(action).Transit(from).To(to)</c></item>
/// <item><b>FromAny 直转</b>：<c>.On(action).FromAny().To(to)</c>（任意状态触发 action 都到 to）</item>
/// </list>
/// </summary>
public sealed class StateMachineBuilder<TState, TAction>
    where TState : notnull where TAction : notnull
{
    private readonly StateMachineDefinition<TState, TAction> _def = new();
    private readonly Dictionary<(TState From, TAction Action), TState> _actionTransitions = [];
    // 🟡 review 修复：FromAny 转换独立存储，避免与精确转换的 (default!, action) 键冲突
    private readonly Dictionary<TAction, TState> _actionTransitionsFromAny = [];
    private readonly Dictionary<TState, HashSet<TState>> _allowed = [];
    private readonly Dictionary<(TState From, TAction Action), List<IGuard>> _guards = [];
    private readonly Dictionary<TAction, List<IGuard>> _guardsFromAny = [];
    private bool _built;

    internal StateMachineBuilder(TState initialState)
    {
        _def.InitialState = initialState;
        _def.HasInitialState = true;
    }

    /// <summary>声明一个动作触发的转换链的入口。</summary>
    public TransitionBuilder On(TAction action) => new(this, action);

    internal void AddActionTransition(TState from, TAction action, TState to)
    {
        _actionTransitions[(from, action)] = to;
        AddAllowed(from, to);
    }

    internal void AddActionTransitionFromAny(TAction action, TState to)
    {
        _actionTransitionsFromAny[action] = to;
    }

    internal void AddAllowed(TState from, params TState[] targets)
    {
        if (!_allowed.TryGetValue(from, out var set))
        {
            set = [];
            _allowed[from] = set;
        }
        foreach (var t in targets) set.Add(t);
    }

    internal void AddGuard(TState from, TAction action, IGuard guard)
    {
        if (!_guards.TryGetValue((from, action), out var list))
        {
            list = [];
            _guards[(from, action)] = list;
        }
        list.Add(guard);
    }

    internal void AddGuardFromAny(TAction action, IGuard guard)
    {
        if (!_guardsFromAny.TryGetValue(action, out var list))
        {
            list = [];
            _guardsFromAny[action] = list;
        }
        list.Add(guard);
    }

    /// <summary>冻结定义并返回。重复调用返回同一实例。</summary>
    public StateMachineDefinition<TState, TAction> Build()
    {
        if (_built) return _def;

        _def.ActionTransitions = _actionTransitions.ToFrozenDictionary();
        _def.ActionTransitionsFromAny = _actionTransitionsFromAny.ToFrozenDictionary();
        _def.AllowedTransitions = _allowed.ToFrozenDictionary(
            kv => kv.Key, kv => kv.Value.ToFrozenSet());
        _def.Guards = _guards.ToFrozenDictionary(
            kv => kv.Key, kv => (IReadOnlyList<IGuard>)kv.Value.ToList());
        _def.GuardsFromAny = _guardsFromAny.ToFrozenDictionary(
            kv => kv.Key, kv => (IReadOnlyList<IGuard>)kv.Value.ToList());
        _def.Freeze();
        _built = true;
        return _def;
    }

    /// <summary>
    /// 单个转换的链式构造器。每次 <see cref="On"/> 产生一个新实例，
    /// 避免"上次 On 的状态"污染本次（同时声明多个 On 互不干扰）。
    /// </summary>
    public sealed class TransitionBuilder
    {
        private readonly StateMachineBuilder<TState, TAction> _owner;
        private readonly TAction _action;
        private TState? _from;
        private bool _fromAny;
        private TState? _to;

        internal TransitionBuilder(StateMachineBuilder<TState, TAction> owner, TAction action)
        {
            _owner = owner;
            _action = action;
        }

        /// <summary>声明转换的起始状态。</summary>
        public TransitionBuilder Transit(TState from)
        {
            _from = from;
            _fromAny = false;
            return this;
        }

        /// <summary>声明可从任意起始状态触发（典型如 Cancelled / Closed）。</summary>
        public TransitionBuilder FromAny()
        {
            _fromAny = true;
            _from = default;
            return this;
        }

        /// <summary>声明目标状态，完成本转换的注册。</summary>
        public GuardBuilder To(TState to)
        {
            _to = to;
            if (_fromAny)
            {
                // 🟡 review 修复：FromAny 转换存独立字典，不与精确转换混淆
                _owner.AddActionTransitionFromAny(_action, to);
            }
            else if (_from is not null)
            {
                _owner.AddActionTransition(_from, _action, to);
            }
            else
            {
                throw new InvalidOperationException(
                    "转换未声明起始状态：先调用 Transit(from) 或 FromAny()，再调用 To(to)。");
            }
            return new GuardBuilder(_owner, _fromAny, _fromAny ? default! : _from!, _action);
        }
    }

    /// <summary>转换上 Guard 的链式构造器（可叠加多个 Guard）。</summary>
    public sealed class GuardBuilder
    {
        private readonly StateMachineBuilder<TState, TAction> _owner;
        private readonly bool _isFromAny;
        private readonly TState _from;
        private readonly TAction _action;

        internal GuardBuilder(StateMachineBuilder<TState, TAction> owner, bool isFromAny, TState from, TAction action)
        {
            _owner = owner;
            _isFromAny = isFromAny;
            _from = from;
            _action = action;
        }

        /// <summary>添加同步守卫。多个 Guard 全部通过才允许转换。</summary>
        public GuardBuilder Guard<TEntity>(GuardDelegate<TEntity> predicate, string reason)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            var guard = new SyncGuard<TEntity>(predicate, reason);
            if (_isFromAny) _owner.AddGuardFromAny(_action, guard);
            else _owner.AddGuard(_from, _action, guard);
            return this;
        }

        /// <summary>添加异步守卫（用于查库存 / 查权限等需 I/O 的检查）。</summary>
        public GuardBuilder GuardAsync<TEntity>(GuardAsyncDelegate<TEntity> predicate, string reason)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            var guard = new AsyncGuard<TEntity>(predicate, reason);
            if (_isFromAny) _owner.AddGuardFromAny(_action, guard);
            else _owner.AddGuard(_from, _action, guard);
            return this;
        }

        /// <summary>返回 owner，继续声明下一个 On(...) 转换。</summary>
        public StateMachineBuilder<TState, TAction> And() => _owner;
    }
}
