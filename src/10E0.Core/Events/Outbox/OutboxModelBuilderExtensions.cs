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
            b.HasIndex(m => new { m.SentTime, m.OccurredOn });
        });
        return mb;
    }
}
