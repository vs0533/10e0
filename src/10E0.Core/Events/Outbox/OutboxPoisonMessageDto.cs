namespace TenE0.Core.Events.Outbox;

/// <summary>
/// 死信（Poison Message）导出 DTO — 仅含排障关键字段，避免把内部实体直接外泄。
///
/// 设计要点：
/// - 与 <see cref="OutboxMessage"/> 字段一一对应，便于离线分析（CSV/JSON 导出）
/// - 不可变 <c>record</c>，无行为；DTO 层与持久化层解耦，未来字段增删不会破坏调用方
/// - <see cref="LastError"/> 允许 null（重试清空后或从未失败过）
/// </summary>
public sealed record OutboxPoisonMessageDto(
    Guid Id,
    string EventType,
    string Payload,
    DateTimeOffset OccurredOn,
    int AttemptCount,
    string? LastError);
