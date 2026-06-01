using Microsoft.EntityFrameworkCore;

namespace TenE0.Core.Permissions.Storage;

public static class PermissionModelBuilderExtensions
{
    /// <summary>
    /// 配置权限相关表。
    /// TRole 泛型支持业务方扩展（如增加图标/颜色等字段）；不需要扩展时传 TenE0Role。
    /// </summary>
    public static ModelBuilder ConfigureTenE0PermissionTables<TRole>(this ModelBuilder modelBuilder)
        where TRole : TenE0Role
    {
        modelBuilder.Entity<TRole>(b =>
        {
            b.Property(r => r.Code).HasMaxLength(64).IsRequired();
            b.HasIndex(r => r.Code).IsUnique();
            b.Property(r => r.Name).HasMaxLength(128).IsRequired();
            b.Property(r => r.Description).HasMaxLength(512);
        });

        modelBuilder.Entity<TenE0RolePermission>(b =>
        {
            b.Property(rp => rp.RoleCode).HasMaxLength(64).IsRequired();
            b.Property(rp => rp.PermissionKey).HasMaxLength(128).IsRequired();
            b.HasIndex(rp => new { rp.RoleCode, rp.PermissionKey }).IsUnique();
            b.HasIndex(rp => rp.RoleCode);
        });

        return modelBuilder;
    }
}
