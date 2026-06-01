using Microsoft.EntityFrameworkCore;
using TenE0.Core.Abstractions;
using TenE0.Core.Auth.Jwt.Services;
using TenE0.Core.Auth.Jwt.Storage;

namespace TenE0.Core.Auth.Jwt.Commands;

/// <summary>
/// 登录命令处理器。
///
/// 泛型 TUser 让业务方扩展 User 字段后框架直接用扩展类型查询（自动 TPH 同表存储）。
/// TContext 仅约束 DbContext，不强制实现任何接口 — TUser 实体由 TenE0SystemDbContext 基类自动注册到 model。
/// </summary>
public sealed class LoginCommandHandler<TUser, TContext>(
    IDbContextFactory<TContext> contextFactory,
    IPasswordHasher passwordHasher,
    IJwtTokenService tokenService,
    IErrs errs)
    : ICommandHandler<LoginCommand, AuthResult>
    where TUser : TenE0User
    where TContext : DbContext
{
    public async Task<AuthResult> HandleAsync(LoginCommand cmd, CancellationToken ct)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);

        var user = await dc.Set<TUser>().FirstOrDefaultAsync(u => u.UserCode == cmd.UserCode, ct);

        // 防 timing attack：找不到用户也跑一次 Verify（耗时一致）
        var verified = user is not null && passwordHasher.Verify(cmd.Password, user.PasswordHash);

        if (user is null || !verified)
        {
            errs.Add("用户名或密码错误", code: "AUTH_INVALID");
            return null!;
        }

        if (!user.IsActive)
        {
            errs.Add("账号已被禁用", code: "AUTH_DISABLED");
            return null!;
        }

        var roles = await dc.Set<TenE0UserRole>()
            .Where(ur => ur.UserCode == user.UserCode)
            .Select(ur => ur.RoleCode)
            .ToListAsync(ct);

        var tokens = tokenService.Issue(user.UserCode, user.DisplayName, user.UserType, roles);

        dc.Set<TenE0RefreshToken>().Add(new TenE0RefreshToken
        {
            TokenHash = tokens.RefreshTokenHash,
            UserCode = user.UserCode,
            ExpiresAt = tokens.RefreshTokenExpiresAt,
            CreatedByIp = cmd.ClientIp,
        });
        await dc.SaveChangesAsync(ct);

        return new AuthResult(
            tokens.AccessToken,
            tokens.AccessTokenExpiresAt,
            tokens.RefreshToken,
            tokens.RefreshTokenExpiresAt,
            user.UserCode,
            user.DisplayName,
            roles);
    }
}
