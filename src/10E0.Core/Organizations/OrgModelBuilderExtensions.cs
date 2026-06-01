using Microsoft.EntityFrameworkCore;

namespace TenE0.Core.Organizations;

public static class OrgModelBuilderExtensions
{
    public static ModelBuilder ConfigureTenE0OrgTables(this ModelBuilder mb)
    {
        mb.Entity<TenE0Org>(b =>
        {
            b.Property(o => o.Code).HasMaxLength(64).IsRequired();
            b.HasIndex(o => o.Code).IsUnique();
            b.Property(o => o.Name).HasMaxLength(128).IsRequired();
            b.Property(o => o.Description).HasMaxLength(512);
            b.Property(o => o.Path).HasMaxLength(512).IsRequired();
            b.HasIndex(o => o.Path);
            b.HasIndex(o => o.ParentId);
        });
        return mb;
    }
}
