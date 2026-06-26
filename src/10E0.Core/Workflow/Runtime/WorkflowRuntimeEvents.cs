using TenE0.Core.Events;

namespace TenE0.Core.Workflow.Runtime;

// ============================================================
// 流程运行时领域事件
//
// 设计：本 PR 只定义事件契约 + 一个日志演示订阅者（见 WorkflowEventLoggerHandler）。
// 推送（#155）和审计（#152）的订阅者属各自 issue，在此消费本组事件。
// ============================================================

/// <summary>流程启动。</summary>
public sealed record ProcessStartedEvent(
    string InstanceId,
    string DefinitionCode,
    int DefinitionVersion,
    string Initiator,
    string StartNodeCode,
    IReadOnlyList<string> InitialAssignees) : IDomainEvent;

/// <summary>流程进入新节点（产生了新任务，审批人需被通知）。</summary>
public sealed record ProcessNodeEnteredEvent(
    string InstanceId,
    string NodeCode,
    string NodeName,
    IReadOnlyList<string> Assignees) : IDomainEvent;

/// <summary>流程完成（全部通过）。</summary>
public sealed record ProcessCompletedEvent(
    string InstanceId,
    string Initiator,
    ProcessStatus FinalStatus,
    DateTimeOffset CompletedAt) : IDomainEvent;

/// <summary>流程被撤销。</summary>
public sealed record ProcessCancelledEvent(
    string InstanceId,
    string Initiator,
    string? Reason) : IDomainEvent;
