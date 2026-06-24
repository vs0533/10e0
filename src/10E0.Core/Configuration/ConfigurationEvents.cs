using TenE0.Core.Events;

namespace TenE0.Core.Configuration;

// ============================================================
// Configuration 模块领域事件
//
// 设计（与 WorkflowRuntimeEvents 一致）：
// - 记录为不可变 record，实现 IDomainEvent，past-tense 命名
// - 仅定义事件契约；订阅者（审计 #152 / 通知 #155）属各自 issue，可消费本组事件
// - 携带足够上下文（typeCode / oldValue / newValue），避免订阅者反查 N+1
// ============================================================

/// <summary>数据字典（类型或选项）发生变更。订阅者可据此清本地缓存或重载。</summary>
/// <param name="DictTypeCode">受影响的字典类型 Code。</param>
/// <param name="Change">变更描述（"type-updated" / "item-added" / "item-removed" 等）。</param>
public sealed record DictChangedEvent(string DictTypeCode, string Change) : IDomainEvent;

/// <summary>系统参数值被修改。订阅者可据此重载本地缓存或执行副作用（如限流阈值变更）。</summary>
/// <param name="Key">系统参数 Key。</param>
/// <param name="OldValue">修改前的值。</param>
/// <param name="NewValue">修改后的值。</param>
public sealed record SystemParameterChangedEvent(string Key, string OldValue, string NewValue) : IDomainEvent;
