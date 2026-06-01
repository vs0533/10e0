using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using TenE0.Core.Auth.Jwt;
using TenE0.Core.Auth.Jwt.Commands;
using TenE0.Core.Auth.Jwt.Services;
using TenE0.Core.Auth.Jwt.Storage;

namespace TenE0.Core.DependencyInjection;

public static class JwtAuthExtensions
{
    /// <summary>
    /// 启用 JWT 认证 + Refresh Token 持久化。
    ///
    /// TUser 泛型让业务方扩展用户字段后能直接在登录流程中使用（自动 TPH 同表存储）。
    /// TContext 仅需是 DbContext —— 框架表由 TenE0SystemDbContext 自动注册。
    ///
    /// 用法：
    ///   services.AddTenE0JwtAuth&lt;TenE0User, AppDbContext&gt;(opt =&gt; ...)  // 不扩展
    ///   services.AddTenE0JwtAuth&lt;AppUser, AppDbContext&gt;(opt =&gt; ...)    // 扩展
    /// </summary>
    public static IServiceCollection AddTenE0JwtAuth<TUser, TContext>(
        this IServiceCollection services,
        Action<JwtOptions> configure)
        where TUser : TenE0User
        where TContext : DbContext
    {
        services.Configure(configure);

        services.TryAddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.TryAddScoped<IJwtTokenService, JwtTokenService>();

        // 泛型命令处理器：手动注册（程序集扫描不能注册开放泛型 + 具体类型组合）
        services.AddScoped<TenE0.Core.Abstractions.ICommandHandler<LoginCommand, AuthResult>,
            LoginCommandHandler<TUser, TContext>>();
        services.AddScoped<TenE0.Core.Abstractions.ICommandHandler<RefreshTokenCommand, AuthResult>,
            RefreshTokenCommandHandler<TUser, TContext>>();
        services.AddScoped<TenE0.Core.Abstractions.ICommandHandler<LogoutCommand, TenE0.Core.Abstractions.Unit>,
            LogoutCommandHandler<TContext>>();

        // ASP.NET Core JWT Bearer 认证方案
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, jwt =>
            {
                using var sp = services.BuildServiceProvider();
                var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<JwtOptions>>().Value;

                // 禁用 inbound claim 自动映射，保留 JWT 原始 claim 名（sub / role / user_type）
                jwt.MapInboundClaims = false;

                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    NameClaimType = TenE0.Core.Abstractions.JwtClaims.Name,
                    RoleClaimType = TenE0.Core.Abstractions.JwtClaims.Role,

                    ValidateIssuer = true,
                    ValidIssuer = opt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = opt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(opt.SigningKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });

        return services;
    }

    /// <summary>非泛型快捷重载 — 不扩展 User 时用 TenE0User。</summary>
    public static IServiceCollection AddTenE0JwtAuth<TContext>(
        this IServiceCollection services,
        Action<JwtOptions> configure)
        where TContext : DbContext
        => services.AddTenE0JwtAuth<TenE0User, TContext>(configure);
}
