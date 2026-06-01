using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.Auth.Jwt;
using TenE0.Core.Auth.Jwt.Storage;
using TenE0.Core.Permissions;
using TenE0.Core.Permissions.Storage;

namespace TenE0.Core.DependencyInjection;

/// <summary>
/// "Identity 模式"一键启用 — 类似 ASP.NET Core Identity 的 services.AddIdentity()。
///
/// 把 JWT + 权限 + 组织 + 流水号 + 领域事件 + Outbox 一次性配置完毕，
/// 业务方只需关注 TUser/TRole/TContext 三个泛型参数。
/// </summary>
public static class IdentityExtensions
{
    /// <summary>
    /// 一行注册框架的完整 Identity 栈。
    ///
    /// 用法（不扩展用户/角色）：
    ///     builder.Services.AddTenE0Identity&lt;AppDbContext&gt;(opt =&gt; { ... });
    ///
    /// 用法（扩展用户字段）：
    ///     public class AppUser : TenE0User { public string? Avatar; }
    ///     builder.Services.AddTenE0Identity&lt;AppUser, AppDbContext&gt;(opt =&gt; { ... });
    ///
    /// 用法（扩展用户+角色）：
    ///     builder.Services.AddTenE0Identity&lt;AppUser, AppRole, AppDbContext&gt;(opt =&gt; { ... });
    /// </summary>
    public static IServiceCollection AddTenE0Identity<TUser, TRole, TContext>(
        this IServiceCollection services,
        Action<TenE0IdentityOptions> configure)
        where TUser : TenE0User
        where TRole : TenE0Role
        where TContext : DbContext
    {
        var opts = new TenE0IdentityOptions();
        configure(opts);

        // JWT 认证 + 三个命令处理器
        services.AddTenE0JwtAuth<TUser, TContext>(jwt =>
        {
            jwt.Issuer = opts.Jwt.Issuer;
            jwt.Audience = opts.Jwt.Audience;
            jwt.SigningKey = opts.Jwt.SigningKey;
            jwt.AccessTokenLifetime = opts.Jwt.AccessTokenLifetime;
            jwt.RefreshTokenLifetime = opts.Jwt.RefreshTokenLifetime;
        });

        // 权限：评估器 + 缓存 + 目录 + Pipeline Behavior
        services.AddTenE0Permissions(perm =>
        {
            foreach (var role in opts.Permissions.SuperUserRoles)
                perm.SuperUserRoles.Add(role);
            // CacheDuration 是 init-only，只能在初始化时设置，这里不做覆盖（按默认 5 分钟）
        });
        services.AddTenE0PermissionStorage<TContext>();

        // 组织树
        services.AddTenE0Organizations<TContext>();

        return services;
    }

    /// <summary>仅扩展 TUser，TRole 用 TenE0Role 默认。</summary>
    public static IServiceCollection AddTenE0Identity<TUser, TContext>(
        this IServiceCollection services,
        Action<TenE0IdentityOptions> configure)
        where TUser : TenE0User
        where TContext : DbContext
        => services.AddTenE0Identity<TUser, TenE0Role, TContext>(configure);

    /// <summary>不扩展用户/角色，全用框架默认类型。</summary>
    public static IServiceCollection AddTenE0Identity<TContext>(
        this IServiceCollection services,
        Action<TenE0IdentityOptions> configure)
        where TContext : DbContext
        => services.AddTenE0Identity<TenE0User, TenE0Role, TContext>(configure);
}

/// <summary>AddTenE0Identity 配置选项。</summary>
public sealed class TenE0IdentityOptions
{
    public JwtOptions Jwt { get; set; } = new()
    {
        Issuer = "10E0",
        Audience = "10E0",
        SigningKey = "",
    };

    public PermissionsOptions Permissions { get; set; } = new();
}
