using Microsoft.EntityFrameworkCore;

namespace TenE0.Core.Auth.Jwt.Storage;

public static class AuthModelBuilderExtensions
{
    /// <summary>
    /// 配置 JWT 认证相关表。
    /// TUser 泛型支持业务方扩展（如增加头像/手机/部门字段）；不需要扩展时传 TenE0User。
    /// </summary>
    public static ModelBuilder ConfigureTenE0AuthTables<TUser>(this ModelBuilder mb)
        where TUser : TenE0User
    {
        mb.Entity<TUser>(b =>
        {
            b.Property(u => u.UserCode).HasMaxLength(64).IsRequired();
            b.HasIndex(u => u.UserCode).IsUnique();
            b.Property(u => u.DisplayName).HasMaxLength(128).IsRequired();
            b.Property(u => u.PasswordHash).HasMaxLength(512).IsRequired();
            b.Property(u => u.Email).HasMaxLength(256);
            b.Property(u => u.Phone).HasMaxLength(32);
            // #11 multi-tenancy: tenantId 可选（系统账号 / 多租户关闭场景）。
            // 长度与 UserCode 保持一致（GUID 字符串 / 业务编码皆可）。
            b.Property(u => u.TenantId).HasMaxLength(64);
            b.HasIndex(u => u.TenantId);
        });

        mb.Entity<TenE0UserRole>(b =>
        {
            b.Property(ur => ur.UserCode).HasMaxLength(64).IsRequired();
            b.Property(ur => ur.RoleCode).HasMaxLength(64).IsRequired();
            b.HasIndex(ur => new { ur.UserCode, ur.RoleCode }).IsUnique();
            b.HasIndex(ur => ur.UserCode);
        });

        mb.Entity<TenE0RefreshToken>(b =>
        {
            b.Property(t => t.TokenHash).HasMaxLength(128).IsRequired();
            b.HasIndex(t => t.TokenHash).IsUnique();
            b.Property(t => t.UserCode).HasMaxLength(64).IsRequired();
            b.HasIndex(t => t.UserCode);
            b.Property(t => t.ReplacedByTokenHash).HasMaxLength(128);
            b.Property(t => t.CreatedByIp).HasMaxLength(64);
            b.Property(t => t.RevokedReason).HasMaxLength(64);
            b.HasIndex(t => t.RevokedAt);
        });

        return mb;
    }
}
