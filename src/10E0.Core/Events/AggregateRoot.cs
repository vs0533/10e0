using System.ComponentModel.DataAnnotations.Schema;
using TenE0.Core.Entities;

namespace TenE0.Core.Events;

/// <summary>
/// 聚合根基类 — 持有未发布的领域事件。
///
/// 在 DDD 模型中：
/// - 聚合根是事务一致性的边界
/// - 业务方法（如 Approve / Cancel / Publish）修改状态并 Raise 事件
/// - SaveChanges 时 OutboxInterceptor 自动把 PendingEvents 写入 Outbox 表
/// - 业务状态 + Outbox 消息在同一个本地事务中提交 — 原子
///
/// 与 AuditedEntity 的关系：
/// - AggregateRoot 继承自 AuditedEntity，所以自带软删除 + 时间戳 + 主键
/// - 不需要事件能力的实体直接用 AuditedEntity 即可（如 Tag / Lookup 表）
/// </summary>
public abstract class AggregateRoot : AuditedEntity
{
    // [NotMapped] — 这个集合不参与 EF 映射，仅在内存中临时持有事件
    [NotMapped]
    private readonly List<IDomainEvent> _pendingEvents = [];

    /// <summary>当前聚合实例上待发布的领域事件（只读快照）。</summary>
    [NotMapped]
    public IReadOnlyList<IDomainEvent> PendingEvents => _pendingEvents;

    /// <summary>由业务方法调用，记录一个领域事件。</summary>
    protected void Raise(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _pendingEvents.Add(domainEvent);
    }

    /// <summary>OutboxInterceptor 把事件取走后调用，避免重复发布。</summary>
    public void ClearEvents() => _pendingEvents.Clear();
}
