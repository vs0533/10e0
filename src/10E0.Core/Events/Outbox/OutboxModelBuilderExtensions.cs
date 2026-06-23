using Microsoft.EntityFrameworkCore;

namespace TenE0.Core.Events.Outbox;

public static class OutboxModelBuilderExtensions
{
    /// <summary>
    /// #127: 列长权威来源集中管理 —— OutboxSchemaSeeder 的 ALTER SQL 必须与此处一致，
    /// 避免实体改 MaxLength 后 seeder ALTER 出旧长度导致漂移。Seeder 通过本常量读取。
    /// </summary>
    public const int LockedByInstanceMaxLength = 128;
    public const int EventTypeMaxLength = 512;
    public const int LastErrorMaxLength = 2048;

    public static ModelBuilder ConfigureTenE0OutboxTables(this ModelBuilder mb)
    {
        mb.Entity<OutboxMessage>(b =>
        {
            b.Property(m => m.EventType).HasMaxLength(EventTypeMaxLength).IsRequired();
            b.Property(m => m.Payload).IsRequired();
            b.Property(m => m.LastError).HasMaxLength(LastErrorMaxLength);
            b.Property(m => m.LockedByInstance).HasMaxLength(LockedByInstanceMaxLength);
            // #107: 增强 pick 查询的索引覆盖。
            // Relay pick: WHERE SentTime IS NULL AND AttemptCount < MaxAttempts ORDER BY OccurredOn
            // 把 AttemptCount 纳入索引前导列，让 relay 跳过已超 MaxAttempts 的毒消息行时走索引扫描。
            b.HasIndex(m => new { m.SentTime, m.AttemptCount, m.OccurredOn });
            // SKIP LOCKED 风格的 WHERE 过滤：Relay 跳过已锁行时按 (LockedUntil, OccurredOn) 走索引扫描
            b.HasIndex(m => new { m.LockedUntil, m.OccurredOn });
        });
        return mb;
    }
}
