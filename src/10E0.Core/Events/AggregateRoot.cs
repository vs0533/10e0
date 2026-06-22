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

    /// <summary>
    /// 框架级入口 — 给 EntityService、BeforeSave 钩子等场景在聚合外部触发事件用。
    /// <para>
    /// 业务方法应继续走 <c>protected Raise</c>（封装边界）。只有框架代码（演示项目、
    /// 通用 BeforeSave 钩子）需要触发的场景才用 <c>RaiseInternal</c>。
    /// </para>
    /// <para>
    /// 设计原因：issue #93 修复 — 之前 DemoEventTrigger 用
    /// <c>BindingFlags.NonPublic</c> 反射调 protected Raise，签名变更（如加 cancellation
    /// token）会让反射静默运行时崩。internal + <c>InternalsVisibleTo</c> 给 demo 项目
    /// 暴露稳定入口，IDE / 分析器可识别，重构不再有"反射盲区"。
    /// </para>
    /// </summary>
    internal void RaiseInternal(IDomainEvent domainEvent) => Raise(domainEvent);

    /// <summary>OutboxInterceptor 把事件取走后调用，避免重复发布。</summary>
    public void ClearEvents() => _pendingEvents.Clear();
}
