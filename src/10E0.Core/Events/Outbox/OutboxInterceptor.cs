using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace TenE0.Core.Events.Outbox;

/// <summary>
/// SaveChanges 拦截器 — 把聚合根的 PendingEvents 写入 OutboxMessage 表。
///
/// 工作时序：
/// 1. 业务代码调 SaveChangesAsync
/// 2. 此拦截器在真正持久化前触发，扫描 ChangeTracker 中所有 AggregateRoot
/// 3. 把它们 PendingEvents 序列化为 OutboxMessage 加入 ChangeTracker
/// 4. 清空聚合的 PendingEvents（避免重复发布）
/// 5. EF 接着把业务实体 + OutboxMessage 一起 SaveChanges → 同一事务原子提交
///
/// 这是 Outbox 模式的核心保证：业务状态变更与事件发布原子化。
/// </summary>
public sealed class OutboxInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
    /// <summary>
    /// #108: 缓存 Type → AssemblyQualifiedName，避免每次 SaveChanges 对每个事件
    /// 重复调用 GetType().AssemblyQualifiedName（每次都拼字符串含版本/公钥 token）。
    /// 事件类型在进程内稳定，缓存命中后零反射开销。
    /// </summary>
    private static readonly ConcurrentDictionary<Type, string> EventTypeNames = new();

    /// <summary>
    /// #108: 复用 JsonSerializerOptions 避免每次 Serialize 创建默认实例。
    /// 注意：必须用指定 inputType 的 Serialize 重载（evt 是 IDomainEvent 接口，
    /// 用静态类型序列化会丢子类属性）。
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new();

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
            ExtractEvents(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        if (eventData.Context is not null)
            ExtractEvents(eventData.Context);
        return result;
    }

    private void ExtractEvents(DbContext context)
    {
        var aggregates = context.ChangeTracker.Entries<AggregateRoot>()
            .Select(e => e.Entity)
            .Where(a => a.PendingEvents.Count > 0)
            .ToList();

        if (aggregates.Count == 0) return;

        var now = timeProvider.GetUtcNow();

        foreach (var aggregate in aggregates)
        {
            foreach (var evt in aggregate.PendingEvents)
            {
                var eventType = evt.GetType();
                var msg = new OutboxMessage
                {
                    EventType = EventTypeNames.GetOrAdd(eventType, static t => t.AssemblyQualifiedName!),
                    Payload = JsonSerializer.Serialize(evt, eventType, SerializerOptions),
                    OccurredOn = now,
                };
                context.Add(msg);
            }
            aggregate.ClearEvents();
        }
    }
}
