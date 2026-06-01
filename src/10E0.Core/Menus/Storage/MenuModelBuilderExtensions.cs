using Microsoft.EntityFrameworkCore;

namespace TenE0.Core.Menus.Storage;

public static class MenuModelBuilderExtensions
{
    public static ModelBuilder ConfigureTenE0MenuTables(this ModelBuilder mb)
    {
        mb.Entity<TenE0Menu>(b =>
        {
            b.Property(m => m.Name).HasMaxLength(100).IsRequired();
            b.HasIndex(m => m.Name).IsUnique();
            b.Property(m => m.RoutePath).HasMaxLength(200).IsRequired();
            b.HasIndex(m => m.RoutePath).IsUnique();
            b.Property(m => m.TreePath).HasMaxLength(512);
            b.HasIndex(m => m.TreePath);
            b.HasIndex(m => m.ParentId);
            b.Property(m => m.Component).HasMaxLength(300);
            b.Property(m => m.Redirect).HasMaxLength(300);
            b.Property(m => m.Icon).HasMaxLength(64);
        });

        mb.Entity<TenE0RoleMenu>(b =>
        {
            b.Property(rm => rm.RoleCode).HasMaxLength(64).IsRequired();
            b.HasIndex(rm => rm.RoleCode);
            b.Property(rm => rm.MenuId).IsRequired();
            b.HasIndex(rm => rm.MenuId);
            b.HasIndex(rm => new { rm.RoleCode, rm.MenuId }).IsUnique();
        });

        return mb;
    }
}
