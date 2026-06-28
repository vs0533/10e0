using TenE0.Core.Events;

namespace TenE0.Core.Scheduling;

/// <summary>
/// 任务执行失败（重试耗尽）领域事件（issue #164）。
///
/// <para>
/// <see cref="JobExecutor{TContext}"/> 在重试耗尽后触发本事件，经 Outbox 持久化后异步分发。
/// 订阅者可据此推送运维通知（关联 #155 推送 / #152 审计）。
/// </para>
///
/// <para>
/// 用 <c>record</c> 保证不可变；自带 JobCode / ErrorMessage / Attempt 上下文，
/// 订阅者无需再查 DB（避免 N+1）。
/// </para>
/// </summary>
public sealed record JobFailedEvent(
    string JobId,
    string JobCode,
    string JobName,
    int Attempt,
    string ErrorMessage,
    DateTimeOffset FailedAt) : IDomainEvent;
