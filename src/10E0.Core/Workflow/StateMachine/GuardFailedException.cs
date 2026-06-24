namespace TenE0.Core.Workflow.StateMachine;

/// <summary>
/// 状态转换的守卫条件（Guard）未通过时抛出。
///
/// 一个转换可声明多个 Guard，全部通过才允许转换。任一失败时本异常携带<b>所有</b>失败的
/// <see cref="Reasons"/>（错误码集合），供前端 i18n 一次性展示，对齐
/// <c>ErrorCodes.cs</c> 的"静态错误码"约定。
/// </summary>
public sealed class GuardFailedException : InvalidOperationException
{
    /// <summary>所有未通过的 Guard 的错误码 / 原因。</summary>
    public IReadOnlyList<string> Reasons { get; }

    public GuardFailedException(IReadOnlyList<string> reasons)
        : base($"状态转换被守卫条件阻止：{string.Join(", ", reasons)}")
    {
        Reasons = reasons;
    }
}
