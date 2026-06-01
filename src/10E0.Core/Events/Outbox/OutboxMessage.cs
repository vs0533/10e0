using TenE0.Core.Entities;

namespace TenE0.Core.Events.Outbox;

/// <summary>
/// Outbox 消息表 — 持久化的待发布事件。
///
/// 关键约定：
/// - 业务变更 + 事件入库 必须在同一个本地事务（OutboxInterceptor 保证）
/// - 后台 Relay 服务轮询此表，投递成功后写 SentTime
/// - SentTime IS NULL 即视为"未发送"
/// - 失败计数和最后错误记录用于排障
/// </summary>
public sealed class OutboxMessage : BaseEntity
{
    /// <summary>事件 CLR 类型全名，Relay 反序列化时需要。</summary>
    public required string EventType { get; set; }

    /// <summary>事件 JSON 序列化结果。</summary>
    public required string Payload { get; set; }

    /// <summary>事件产生时间。</summary>
    public required DateTimeOffset OccurredOn { get; set; }

    /// <summary>投递成功的时间；NULL 表示待发送。</summary>
    public DateTimeOffset? SentTime { get; set; }

    /// <summary>已尝试投递的次数。</summary>
    public int AttemptCount { get; set; }

    /// <summary>最后一次失败的简要原因（用于排障）。</summary>
    public string? LastError { get; set; }
}
