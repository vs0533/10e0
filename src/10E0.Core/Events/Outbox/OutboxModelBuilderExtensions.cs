using Microsoft.EntityFrameworkCore;

namespace TenE0.Core.Events.Outbox;

public static class OutboxModelBuilderExtensions
{
    public static ModelBuilder ConfigureTenE0OutboxTables(this ModelBuilder mb)
    {
        mb.Entity<OutboxMessage>(b =>
        {
            b.Property(m => m.EventType).HasMaxLength(512).IsRequired();
            b.Property(m => m.Payload).IsRequired();
            b.Property(m => m.LastError).HasMaxLength(2048);
            b.Property(m => m.LockedByInstance).HasMaxLength(128);
            b.HasIndex(m => new { m.SentTime, m.OccurredOn });
            // SKIP LOCKED 风格的 WHERE 过滤：Relay 跳过已锁行时按 (LockedUntil, OccurredOn) 走索引扫描
            b.HasIndex(m => new { m.LockedUntil, m.OccurredOn });
        });
        return mb;
    }
}
