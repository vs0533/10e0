using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenE0.Core.Abstractions;
using TenE0.Core.Auditing;
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
    ILogger<RefreshTokenCommandHandler<TUser, TContext>> logger,
    IAuditLogSink auditSink)
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
            // #152 登录日志埋点：refresh 失败（token 无效）
            await auditSink.WriteLoginAsync(new LoginLogEntry
            {
                UserCode = "",
                EventType = "Failed",
                Success = false,
                IpAddress = cmd.ClientIp,
                FailureReason = "refresh token 无效",
            }, ct);
            errs.Add("refresh token 无效", code: ErrorCodes.TokenInvalid);
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
            // 清空 ReplacedByTokenHash：重放事件不应再指向"被强制作废的新 token"。
            // 攻击者拿到 401 后可能顺 hash 撞库（DB 已撤销新 token，但攻击信号没被运营系统识别），
            // 也避免重放事件污染审计/取证系统的 token 链上下游视图（issue #117）。
            record.ReplacedByTokenHash = null;
            await dc.SaveChangesAsync(ct);

            // #152 登录日志埋点：refresh 失败（token 重放检测）
            await auditSink.WriteLoginAsync(new LoginLogEntry
            {
                UserCode = record.UserCode,
                EventType = "Failed",
                Success = false,
                IpAddress = cmd.ClientIp,
                FailureReason = "refresh token 重放检测",
            }, ct);
            errs.Add("refresh token 已撤销，请重新登录", code: ErrorCodes.TokenRevoked);
            return null!;
        }

        if (now >= record.ExpiresAt)
        {
            // #152 登录日志埋点：refresh 失败（token 过期）
            await auditSink.WriteLoginAsync(new LoginLogEntry
            {
                UserCode = record.UserCode,
                EventType = "Failed",
                Success = false,
                IpAddress = cmd.ClientIp,
                FailureReason = "refresh token 已过期",
            }, ct);
            errs.Add("refresh token 已过期", code: ErrorCodes.TokenExpired);
            return null!;
        }

        // #102: 合并 user(②) + roles(③) 为单次 JOIN 查询，从 4 次顺序查询降到 3 次。
        // 用 left join 扁平化：user 字段对每个 role 重复；内存里 group 取 user 唯一值 + roles 列表。
        // 不用导航属性 —— TenE0UserRole 是平铺中间表无 SkipNavigation 配置。
        var userRows = await (
            from u in dc.Set<TUser>()
            where u.UserCode == record.UserCode
            join ur in dc.Set<TenE0UserRole>() on u.UserCode equals ur.UserCode into roleJoin
            from ur in roleJoin.DefaultIfEmpty()
            select new { u.IsActive, u.DisplayName, u.UserType, u.TenantId, u.OrgId, u.UserCode, RoleCode = ur != null ? ur.RoleCode : null }
        ).AsNoTracking().ToListAsync(ct);

        // 无 user 行 = 用户不存在；任一行 IsActive=false 即账号禁用
        if (userRows.Count == 0 || !userRows[0].IsActive)
        {
            // #152 登录日志埋点：refresh 失败（账号不可用）
            await auditSink.WriteLoginAsync(new LoginLogEntry
            {
                UserCode = record.UserCode,
                EventType = "Failed",
                Success = false,
                IpAddress = cmd.ClientIp,
                FailureReason = "账号不可用",
            }, ct);
            errs.Add("账号不可用", code: ErrorCodes.AuthDisabled);
            return null!;
        }

        var userRow = userRows[0];
        var roles = userRows
            .Where(r => r.RoleCode is not null)
            .Select(r => r.RoleCode!)
            .Distinct()
            .ToList();

        // #7: 刷新必须用 latest version（不能在旧 token 的 version 上签发）
        var roleVersions = roles.Count == 0
            ? new Dictionary<string, long>()
            : await dc.Set<TenE0Role>()
                .AsNoTracking()
                .Where(r => roles.Contains(r.Code))
                .ToDictionaryAsync(r => r.Code, r => r.Version, StringComparer.Ordinal, ct);

        // #11/#155: refresh 必须用 DB 最新 TenantId 与 OrgId（admin 把人迁走/用户在多租户或组织间切换都能生效）
        var tokens = tokenService.Issue(userRow.UserCode, userRow.DisplayName, userRow.UserType, roles, roleVersions, userRow.TenantId, userRow.OrgId);

        // 滑动过期：新 refresh token 的过期时间刷新为 now + RefreshTokenLifetime
        // 关闭滑动时保留原 token 的剩余有效期；按 JWT 'exp' 语义 record.ExpiresAt == now 视为已到期，
        // 此时回退为重新签发完整 lifetime（也覆盖 record.ExpiresAt < now 的防御性兜底）。
        var newRefreshExpires = slidingEnabled
            ? now.Add(options.RefreshTokenLifetime)
            : (record.ExpiresAt > now ? record.ExpiresAt : now.Add(options.RefreshTokenLifetime));

        if (rotationEnabled)
        {
            // 旋转：原子化撤销旧 token + 写入新 token。
            // 用 ExecuteUpdateAsync + WHERE RevokedAt IS NULL 原子化：并发请求中只有 1 个能成功撤销，
            // 竞争失败方（rows=0）走 reuse-detection 路径（issue #94 修复）。
            // EF Core 9 直发 UPDATE，避免 tracked record 的 TOCTOU 窗口。
            //
            // ⚠️ 已知简化：Microsoft.EntityFrameworkCore.InMemory provider 不支持 ExecuteUpdateAsync
            // （设计上 provider 模拟内存而不实现 SQL UPDATE 语义）。InMemory 测试场景（单线程、
            // 无真并发）下走 fallback 路径：tracked record + SaveChanges，覆盖业务逻辑分支但不验证
            // 原子化。生产 SQL Server / PostgreSQL 走 ExecuteUpdateAsync 真原子化路径。
            int rows;
            // ⚠️ Microsoft.EntityFrameworkCore.InMemory provider 不支持 ExecuteUpdateAsync
            // （设计上 provider 模拟内存而不实现 SQL UPDATE 语义）。InMemory 测试场景
            // （单线程、无真并发）下走 fallback 路径，让单测能跑。生产 SQL Server /
            // PostgreSQL 走 ExecuteUpdateAsync 真原子化路径（真并发验证见
            // RefreshTokenRotationConcurrencyAcceptanceTests）。
            if (dc.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
            {
                // InMemory fallback：保留旧 tracked record 路径，让单测能跑
                var existing = await dc.Set<TenE0RefreshToken>()
                    .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
                if (existing is null || existing.RevokedAt is not null)
                {
                    // tracked record 路径下几乎不会进这里（前面 FirstOrDefaultAsync 已查过）
                    // 但保留兜底防止漏判
                    errs.Add("refresh token 已撤销，请重新登录", code: ErrorCodes.TokenRevoked);
                    return null!;
                }
                existing.RevokedAt = now;
                existing.RevokedReason = RevokedReasonRotated;
                existing.ReplacedByTokenHash = tokens.RefreshTokenHash;
                rows = 1;
            }
            else
            {
                rows = await dc.Set<TenE0RefreshToken>()
                    .Where(t => t.TokenHash == hash && t.RevokedAt == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(t => t.RevokedAt, _ => now)
                        .SetProperty(t => t.RevokedReason, _ => RevokedReasonRotated)
                        .SetProperty(t => t.ReplacedByTokenHash, _ => tokens.RefreshTokenHash),
                        ct);
            }

            if (rows == 0)
            {
                // 竞争失败：另一并发请求已撤销该 token。走 reuse-detection 路径撤销用户全链。
                logger.LogWarning(
                    "检测到 refresh token 旋转竞争失败（issue #94）：UserCode={User}，触发 reuse-detection 撤销该用户全链",
                    userRow.UserCode);

                var active = await dc.Set<TenE0RefreshToken>()
                    .Where(t => t.UserCode == userRow.UserCode && t.RevokedAt == null)
                    .ToListAsync(ct);
                foreach (var t in active) t.RevokedAt = now;
                await dc.SaveChangesAsync(ct);

                // #152 登录日志埋点：refresh 失败（旋转竞争失败 → reuse-detection）
                await auditSink.WriteLoginAsync(new LoginLogEntry
                {
                    UserCode = userRow.UserCode,
                    EventType = "Failed",
                    Success = false,
                    IpAddress = cmd.ClientIp,
                    FailureReason = "refresh token 旋转竞争失败",
                }, ct);
                errs.Add("refresh token 已撤销，请重新登录", code: ErrorCodes.TokenRevoked);
                return null!;
            }

            dc.Set<TenE0RefreshToken>().Add(new TenE0RefreshToken
            {
                TokenHash = tokens.RefreshTokenHash,
                UserCode = userRow.UserCode,
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
                UserCode = userRow.UserCode,
                ExpiresAt = newRefreshExpires,
                CreatedByIp = cmd.ClientIp,
            });
        }

        await dc.SaveChangesAsync(ct);

        // #152 登录日志埋点：refresh 成功
        await auditSink.WriteLoginAsync(new LoginLogEntry
        {
            UserCode = userRow.UserCode,
            EventType = "Refresh",
            Success = true,
            IpAddress = cmd.ClientIp,
            ExpiresAt = newRefreshExpires,
        }, ct);

        return new AuthResult(
            tokens.AccessToken,
            tokens.AccessTokenExpiresAt,
            tokens.RefreshToken,
            newRefreshExpires,
            userRow.UserCode,
            userRow.DisplayName,
            roles);
    }
}
