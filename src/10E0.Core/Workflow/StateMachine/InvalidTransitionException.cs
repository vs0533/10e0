using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace TenE0.Core.Workflow.StateMachine;

/// <summary>
/// 试图执行状态机中未声明的非法转换时抛出。
///
/// 携带 from / to / action 上下文，便于运维从异常直接定位是哪个状态 + 动作组合配置缺失，
/// 而非裸"非法转换"。
/// </summary>
public sealed class InvalidTransitionException<TState, TAction> : InvalidOperationException
    where TState : notnull where TAction : notnull
{
    /// <summary>起始状态。</summary>
    public TState From { get; }

    /// <summary>试图到达的状态（动作触发场景下可能未解析出目标，此时 <see cref="HasTo"/> 为 false）。</summary>
    [MaybeNull]
    public TState To { get; }

    /// <summary><see cref="To"/> 是否有值（值类型 enum 下 To 不能为 null）。</summary>
    [MemberNotNullWhen(true, nameof(To))]
    public bool HasTo { get; }

    /// <summary>触发动作（直转场景可能为 null）。</summary>
    public TAction? Action { get; }

    /// <summary>未解析出目标的非法转换（典型场景：Action 未注册）。</summary>
    public InvalidTransitionException(TState from, TAction? action)
        : base(BuildMessage(from, action, toString: null))
    {
        From = from;
        Action = action;
        HasTo = false;
    }

    /// <summary>已解析出目标但被白名单拒绝的非法转换。</summary>
    public InvalidTransitionException(TState from, TAction? action, TState to)
        : base(BuildMessage(from, action, to.ToString()))
    {
        From = from;
        Action = action;
        To = to;
        HasTo = true;
    }

    private static string BuildMessage(TState from, TAction? action, string? toString)
    {
        var actionPart = action is null ? "(no action)" : action.ToString();
        var toPart = toString ?? "(unknown target)";
        return string.Format(
            CultureInfo.InvariantCulture,
            "非法状态转换：From={0} Action={1} To={2}。该组合未在状态机定义中声明。",
            from, actionPart, toPart);
    }
}
