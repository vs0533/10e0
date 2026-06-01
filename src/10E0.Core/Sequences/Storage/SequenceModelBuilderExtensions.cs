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
        });
        return mb;
    }
}
