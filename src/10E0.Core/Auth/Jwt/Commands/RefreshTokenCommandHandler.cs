using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TenE0.Core.Abstractions;
using TenE0.Core.Auth.Jwt.Services;
using TenE0.Core.Auth.Jwt.Storage;

namespace TenE0.Core.Auth.Jwt.Commands;

/// <summary>
/// Refresh token 旋转处理器。
///
/// 安全策略：
/// 1. 收到 refresh token → 算 SHA-256 → 查 DB
/// 2. 找不到 / 已过期 → 拒绝
/// 3. 已 revoked → 强信号 token 被泄露重放，撤销该用户所有 active token
/// 4. 正常情况：标记旧 token revoked + 串 ReplacedByTokenHash + 写新 token
/// </summary>
public sealed class RefreshTokenCommandHandler<TUser, TContext>(
    IDbContextFactory<TContext> contextFactory,
    IJwtTokenService tokenService,
    TimeProvider timeProvider,
    IErrs errs,
    ILogger<RefreshTokenCommandHandler<TUser, TContext>> logger)
    : ICommandHandler<RefreshTokenCommand, AuthResult>
    where TUser : TenE0User
    where TContext : DbContext
{
    public async Task<AuthResult> HandleAsync(RefreshTokenCommand cmd, CancellationToken ct)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);

        var hash = tokenService.HashRefreshToken(cmd.RefreshToken);
        var record = await dc.Set<TenE0RefreshToken>().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (record is null)
        {
            errs.Add("refresh token 无效", code: "TOKEN_INVALID");
            return null!;
        }

        var now = timeProvider.GetUtcNow();

        if (record.RevokedAt is not null)
        {
            logger.LogWarning(
                "检测到已撤销的 refresh token 被重放：UserCode={User}，撤销该用户全部 active token",
                record.UserCode);

            var active = await dc.Set<TenE0RefreshToken>()
                .Where(t => t.UserCode == record.UserCode && t.RevokedAt == null)
                .ToListAsync(ct);
            foreach (var t in active) t.RevokedAt = now;
            await dc.SaveChangesAsync(ct);

            errs.Add("refresh token 已撤销，请重新登录", code: "TOKEN_REVOKED");
            return null!;
        }

        if (now >= record.ExpiresAt)
        {
            errs.Add("refresh token 已过期", code: "TOKEN_EXPIRED");
            return null!;
        }

        var user = await dc.Set<TUser>().FirstOrDefaultAsync(u => u.UserCode == record.UserCode, ct);
        if (user is null || !user.IsActive)
        {
            errs.Add("账号不可用", code: "AUTH_DISABLED");
            return null!;
        }

        var roles = await dc.Set<TenE0UserRole>()
            .Where(ur => ur.UserCode == user.UserCode)
            .Select(ur => ur.RoleCode)
            .ToListAsync(ct);

        var tokens = tokenService.Issue(user.UserCode, user.DisplayName, user.UserType, roles);

        record.RevokedAt = now;
        record.ReplacedByTokenHash = tokens.RefreshTokenHash;
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
