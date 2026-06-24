using System.Collections.Frozen;

namespace TenE0.Core.Workflow.StateMachine;

/// <summary>
/// 异步守卫委托。返回 true 允许转换，false 阻止（reason 记入失败集合）。
/// </summary>
/// <typeparam name="TEntity">触发转换的实体类型。</typeparam>
/// <param name="entity">实体实例（业务方可直接读字段判断）。</param>
/// <param name="cancellationToken">取消令牌。</param>
public delegate Task<bool> GuardAsyncDelegate<TEntity>(TEntity entity, CancellationToken cancellationToken);

/// <summary>
/// 同步守卫委托。返回 true 允许转换，false 阻止。
/// </summary>
public delegate bool GuardDelegate<TEntity>(TEntity entity);

/// <summary>
/// 状态机定义 — 不可变（Freeze 后只读）。
///
/// 设计要点：
/// <list type="bullet">
/// <item><b>两种转换语义并存</b>：
///   <see cref="ActionTransitions"/> = "在状态 A 触发动作 X → 到达 B"（动作驱动，最常用）；
///   <see cref="AllowedTransitions"/> = "A→B 白名单"（FromAny / 直转场景）。</item>
/// <item><b>不可变</b>：构造期 freeze，运行时只读，可静态缓存避免重复构造。</item>
/// <item><b>Frozen 字典</b>：O(1) 查找，编译期类型安全。</item>
/// </list>
///
/// 不直接 new — 使用 <see cref="StateMachineBuilder{TState,TAction}"/> 构建。
/// </summary>
public sealed class StateMachineDefinition<TState, TAction>
    where TState : notnull where TAction : notnull
{
    /// <summary>初始状态（<see cref="StateMachineBuilder{TState,TAction}"/> 必填）。</summary>
    public TState InitialState { get; internal set; } = default!;

    /// <summary>
    /// 动作驱动转换：(From, Action) → To。
    /// 例如 (Draft, Submit) → Submitted。
    /// 仅含<b>精确状态</b>声明的转换（不含 FromAny，后者见 <see cref="ActionTransitionsFromAny"/>）。
    /// </summary>
    public FrozenDictionary<(TState From, TAction Action), TState> ActionTransitions { get; internal set; }
        = FrozenDictionary<(TState, TAction), TState>.Empty;

    /// <summary>
    /// FromAny 转换：Action → To（独立存储，避免与精确转换的 (default!, action) 键冲突）。
    /// 🟡 review 修复：之前 FromAny 用 (default!, action) 混入 ActionTransitions，
    /// 当 TState 是 enum 时 default == 第一个枚举值（如 Draft=0），与显式 .Transit(Draft)
    /// 声明的转换键冲突 → 后注册者静默覆盖前者。独立字典彻底隔离两条查找路径。
    /// </summary>
    public FrozenDictionary<TAction, TState> ActionTransitionsFromAny { get; internal set; }
        = FrozenDictionary<TAction, TState>.Empty;

    /// <summary>
    /// 白名单直转：From → 允许的目标状态集合（FromAny 注册的目标归入此表对应 From 行）。
    /// 用于运行时校验"是否允许 A→B"（不关心动作时）。
    /// </summary>
    public FrozenDictionary<TState, FrozenSet<TState>> AllowedTransitions { get; internal set; }
        = FrozenDictionary<TState, FrozenSet<TState>>.Empty;

    /// <summary>
    /// 守卫条件：(From, Action) → 该转换上声明的所有 Guard（同步 + 异步混合）。
    /// 仅含精确状态声明的转换的 Guard。
    /// </summary>
    internal FrozenDictionary<(TState From, TAction Action), IReadOnlyList<IGuard>> Guards { get; set; }
        = FrozenDictionary<(TState, TAction), IReadOnlyList<IGuard>>.Empty;

    /// <summary>FromAny 转换的 Guard：Action → Guard 列表（独立存储，同 ActionTransitionsFromAny 理由）。</summary>
    internal FrozenDictionary<TAction, IReadOnlyList<IGuard>> GuardsFromAny { get; set; }
        = FrozenDictionary<TAction, IReadOnlyList<IGuard>>.Empty;

    /// <summary>是否已冻结（freeze 后 <see cref="Freeze"/> 再调无效）。</summary>
    public bool IsFrozen { get; private set; }

    /// <summary>是否已设置初始状态（enum 下 InitialState 默认值不等于"未设置"，故用本标记）。</summary>
    internal bool HasInitialState { get; set; }

    internal void Freeze()
    {
        if (IsFrozen) return;
        if (!HasInitialState)
            throw new InvalidOperationException("状态机定义未设置初始状态（InitialState）");
        IsFrozen = true;
    }

    internal void EnsureFrozen()
    {
        if (!IsFrozen)
            throw new InvalidOperationException("状态机定义未冻结，调用 Build() 后才能使用。");
    }
}

/// <summary>
/// Guard 的类型擦除抽象（同步/异步统一）。运行时按 entity 类型匹配并调用。
/// </summary>
internal interface IGuard
{
    string Reason { get; }
    bool CanHandle(Type entityType);
    Task<bool> EvaluateAsync(object entity, CancellationToken ct);
}

/// <summary>同步 Guard 包装。</summary>
internal sealed class SyncGuard<TEntity>(GuardDelegate<TEntity> predicate, string reason) : IGuard
{
    private readonly GuardDelegate<TEntity> _predicate = predicate;
    public string Reason { get; } = reason;
    public bool CanHandle(Type entityType) => typeof(TEntity).IsAssignableFrom(entityType);
    public Task<bool> EvaluateAsync(object entity, CancellationToken ct)
        => Task.FromResult(_predicate((TEntity)entity));
}

/// <summary>异步 Guard 包装。</summary>
internal sealed class AsyncGuard<TEntity>(GuardAsyncDelegate<TEntity> predicate, string reason) : IGuard
{
    private readonly GuardAsyncDelegate<TEntity> _predicate = predicate;
    public string Reason { get; } = reason;
    public bool CanHandle(Type entityType) => typeof(TEntity).IsAssignableFrom(entityType);
    public Task<bool> EvaluateAsync(object entity, CancellationToken ct)
        => _predicate((TEntity)entity, ct);
}
