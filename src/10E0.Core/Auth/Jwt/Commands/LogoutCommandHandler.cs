using Microsoft.EntityFrameworkCore;
using TenE0.Core.Abstractions;
using TenE0.Core.Auditing;
using TenE0.Core.Auth.Jwt.Services;
using TenE0.Core.Auth.Jwt.Storage;

namespace TenE0.Core.Auth.Jwt.Commands;

/// <summary>Logout 不涉及 User 类型，只标记 refresh token 为 revoked。</summary>
public sealed class LogoutCommandHandler<TContext>(
    IDbContextFactory<TContext> contextFactory,
    IJwtTokenService tokenService,
    TimeProvider timeProvider,
    IAuditLogSink auditSink)
    : ICommandHandler<LogoutCommand, Unit>
    where TContext : DbContext
{
    public async Task<Unit> HandleAsync(LogoutCommand cmd, CancellationToken ct)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);
        var hash = tokenService.HashRefreshToken(cmd.RefreshToken);

        var record = await dc.Set<TenE0RefreshToken>().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (record is null || record.RevokedAt is not null)
            return Unit.Value;   // 幂等

        record.RevokedAt = timeProvider.GetUtcNow();
        await dc.SaveChangesAsync(ct);

        // #152 登录日志埋点：登出成功
        await auditSink.WriteLoginAsync(new LoginLogEntry
        {
            UserCode = record.UserCode,
            EventType = "Logout",
            Success = true,
        }, ct);

        return Unit.Value;
    }
}
