namespace TenE0.Core.Workflow.StateMachine;

/// <summary>
/// 标注实体使用状态机（约定驱动）。
///
/// 此 attribute 仅作<b>声明性标记</b>，本身不触发任何运行时行为 — 状态机的转换规则
/// 仍由 <see cref="IStateMachineDefinition{TState,TAction}"/> 在启动时注册。
/// 它的价值在于：
/// <list type="bullet">
/// <item>代码可读性 — 一眼看出该实体有状态机管控</item>
/// <item>未来工具支持 — 静态分析 / 文档生成可扫描此 attribute 列出全系统状态机</item>
/// </list>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class StateMachineAttribute : Attribute
{
    /// <summary>状态类型（通常是 enum）。</summary>
    public Type StateType { get; }

    /// <summary>动作类型（通常是 enum）。</summary>
    public Type ActionType { get; }

    public StateMachineAttribute(Type stateType, Type actionType)
    {
        StateType = stateType ?? throw new ArgumentNullException(nameof(stateType));
        ActionType = actionType ?? throw new ArgumentNullException(nameof(actionType));
    }
}
