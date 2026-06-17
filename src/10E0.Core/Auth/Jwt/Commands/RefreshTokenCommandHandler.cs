using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenE0.Core.Abstractions;
using TenE0.Core.Auth.Jwt.Services;
using TenE0.Core.Auth.Jwt.Storage;
using TenE0.Core.Permissions.Storage;

namespace TenE0.Core.Auth.Jwt.Commands;

/// <summary>
/// Refresh token 旋转 + 滑动过期处理器。
///
/// 安全策略（OWASP 推荐 — Refresh Token Rotation）：
/// 1. 收到 refresh token → 算 SHA-256 → 查 DB
/// 2. 找不到 / 已过期 → 拒绝
/// 3. 已 revoked → 强信号 token 被泄露重放，撤销该用户所有 active token
/// 4. 正常情况：标记旧 token revoked + reason="rotated" + 串 ReplacedByTokenHash + 写新 token
///
/// 滑动过期（Sliding Expiration）：
/// - 每次成功 refresh：新 refresh token 的 expiry = now + RefreshTokenLifetime
/// - 用户持续活跃则 token 永远不会过期；连续 14 天（默认）不活动才过期
/// - 可由 JwtOptions.SlidingRefreshExpiration 关闭（保持原过期时间）
/// </summary>
public sealed class RefreshTokenCommandHandler<TUser, TContext>(
    IDbContextFactory<TContext> contextFactory,
    IJwtTokenService tokenService,
    TimeProvider timeProvider,
    IOptions<JwtOptions> jwtOptions,
    IErrs errs,
    ILogger<RefreshTokenCommandHandler<TUser, TContext>> logger)
    : ICommandHandler<RefreshTokenCommand, AuthResult>
    where TUser : TenE0User
    where TContext : DbContext
{
    private const string RevokedReasonRotated = "rotated";
    private const string RevokedReasonReuseDetected = "token_reuse_detected";

    public async Task<AuthResult> HandleAsync(RefreshTokenCommand cmd, CancellationToken ct)
    {
        var options = jwtOptions.Value;
        var rotationEnabled = options.RefreshTokenRotationEnabled;
        var slidingEnabled = options.SlidingRefreshExpiration;

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

            // 不管 rotationEnabled 状态如何，重放检测永远撤销全链
            var active = await dc.Set<TenE0RefreshToken>()
                .Where(t => t.UserCode == record.UserCode && t.RevokedAt == null)
                .ToListAsync(ct);
            foreach (var t in active) t.RevokedAt = now;
            // 重放事件比轮换事件更严重：覆盖原 reason 为 token_reuse_detected
            record.RevokedReason = RevokedReasonReuseDetected;
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

        // #7: 刷新必须用 latest version（不能在旧 token 的 version 上签发）
        var roleVersions = roles.Count == 0
            ? new Dictionary<string, long>()
            : await dc.Set<TenE0Role>()
                .AsNoTracking()
                .Where(r => roles.Contains(r.Code))
                .ToDictionaryAsync(r => r.Code, r => r.Version, StringComparer.Ordinal, ct);

        // #11: refresh 必须用 DB 最新 TenantId（admin 把人迁走/用户在多租户间切换都能生效）
        var tokens = tokenService.Issue(user.UserCode, user.DisplayName, user.UserType, roles, roleVersions, user.TenantId);

        // 滑动过期：新 refresh token 的过期时间刷新为 now + RefreshTokenLifetime
        // 关闭滑动时保留原 token 的剩余有效期；按 JWT 'exp' 语义 record.ExpiresAt == now 视为已到期，
        // 此时回退为重新签发完整 lifetime（也覆盖 record.ExpiresAt < now 的防御性兜底）。
        var newRefreshExpires = slidingEnabled
            ? now.Add(options.RefreshTokenLifetime)
            : (record.ExpiresAt > now ? record.ExpiresAt : now.Add(options.RefreshTokenLifetime));

        if (rotationEnabled)
        {
            // 旋转：撤销旧 token + 写入新 token
            record.RevokedAt = now;
            record.RevokedReason = RevokedReasonRotated;
            record.ReplacedByTokenHash = tokens.RefreshTokenHash;
            dc.Set<TenE0RefreshToken>().Add(new TenE0RefreshToken
            {
                TokenHash = tokens.RefreshTokenHash,
                UserCode = user.UserCode,
                ExpiresAt = newRefreshExpires,
                CreatedByIp = cmd.ClientIp,
            });
        }
        else
        {
            // 非旋转模式：直接签发新对，旧 token 仍有效（不推荐）
            logger.LogWarning(
                "RefreshTokenRotationEnabled=false：旧 refresh token 不会被撤销（不推荐，存在长期复用风险）");
            dc.Set<TenE0RefreshToken>().Add(new TenE0RefreshToken
            {
                TokenHash = tokens.RefreshTokenHash,
                UserCode = user.UserCode,
                ExpiresAt = newRefreshExpires,
                CreatedByIp = cmd.ClientIp,
            });
        }

        await dc.SaveChangesAsync(ct);

        return new AuthResult(
            tokens.AccessToken,
            tokens.AccessTokenExpiresAt,
            tokens.RefreshToken,
            newRefreshExpires,
            user.UserCode,
            user.DisplayName,
            roles);
    }
}
