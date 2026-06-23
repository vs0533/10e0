namespace TenE0.Core.Workflow.StateMachine;

/// <summary>
/// 一次状态转换的不可变记录。
///
/// 由 <see cref="StateMachine{TState,TAction}.FireAsync"/> 在转换成功后返回，
/// 携带完整的审计上下文（谁在何时把状态从哪改到哪、为什么）。
/// 业务方通常把它落审计表 / 写入 <c>TenE0ProcessHistory</c> / 作为事件载荷。
/// </summary>
public sealed record StateTransition<TState, TAction>
    where TState : notnull where TAction : notnull
{
    /// <summary>起始状态。</summary>
    public required TState From { get; init; }

    /// <summary>目标状态。</summary>
    public required TState To { get; init; }

    /// <summary>触发的动作（<c>FromAny</c> 直转场景可能为 null）。</summary>
    public TAction? Action { get; init; }

    /// <summary>触发者（用户 code / 系统标识）。空表示匿名系统操作。</summary>
    public string Actor { get; init; } = string.Empty;

    /// <summary>触发原因 / 备注（对应 Guard 失败时使用的错误码字段，此处用于正向审计）。</summary>
    public string? Reason { get; init; }

    /// <summary>转换发生时间（UTC）。</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
