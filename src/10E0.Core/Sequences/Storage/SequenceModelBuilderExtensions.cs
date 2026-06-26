using Microsoft.EntityFrameworkCore;

namespace TenE0.Core.Sequences.Storage;

public static class SequenceModelBuilderExtensions
{
    public static ModelBuilder ConfigureTenE0SequenceTables(this ModelBuilder mb)
    {
        mb.Entity<TenE0Sequence>(b =>
        {
            b.Property(s => s.SequenceKey).HasMaxLength(64).IsRequired();
            b.HasIndex(s => s.SequenceKey).IsUnique();
            b.Property(s => s.CurrentBucket).HasMaxLength(32).IsRequired();

            // #100: 乐观并发控制 shadow property。让 EfSequenceGenerator 的 SELECT+UPDATE 两往返
            // 在并发 UPDATE 同一行时，由 EF Core 校验 RowVersion 抛 DbUpdateConcurrencyException →
            // 触发重试，消除 lost update（SQL Server 映射为 rowversion，Postgres 映射为 xmin）。
            // shadow property：不污染业务实体（TenE0Sequence 无对应 C# 字段）。
            b.Property<byte[]>("RowVersion").IsRowVersion();
        });
        return mb;
    }
}
