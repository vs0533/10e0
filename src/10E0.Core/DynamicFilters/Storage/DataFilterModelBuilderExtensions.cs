using Microsoft.EntityFrameworkCore;

namespace TenE0.Core.DynamicFilters.Storage;

public static class DataFilterModelBuilderExtensions
{
    public static ModelBuilder ConfigureTenE0DataFilterTables(this ModelBuilder mb)
    {
        mb.Entity<TenE0DataFilterRule>(b =>
        {
            b.ToTable("DataFilterRules");
            b.Property(r => r.EntityTypeName).HasMaxLength(200).IsRequired();
            b.HasIndex(r => r.EntityTypeName).IsUnique();
            b.Property(r => r.RuleJson).HasMaxLength(4000).IsRequired();
            b.Property(r => r.Description).HasMaxLength(500);
        });
        return mb;
    }
}
