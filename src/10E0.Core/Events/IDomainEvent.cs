namespace TenE0.Core.Events;

/// <summary>
/// 领域事件标记接口。
///
/// 设计原则：
/// - 事件命名用过去式（XxxCreatedEvent / XxxApprovedEvent），描述"已经发生的事实"
/// - 事件应该是不可变的（用 record 类型）
/// - 事件应该自带足够上下文（不要只丢一个 Id 让订阅者再查 DB，避免 N+1 性能问题）
/// - 事件必须 JSON 可序列化（Outbox 存的是 JSON 串）
///
/// 用法：
///     public sealed record CourseApprovedEvent(
///         string CourseId, string ApproverCode, DateTimeOffset ApprovedAt) : IDomainEvent;
/// </summary>
public interface IDomainEvent
{
}
