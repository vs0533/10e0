using Microsoft.EntityFrameworkCore;
using TenE0.Core.Abstractions;
using TenE0.Core.Auditing;
using TenE0.Core.Auth.Jwt.Services;
using TenE0.Core.Auth.Jwt.Storage;
using TenE0.Core.Permissions.Storage;
using TenE0.Core.Security.LoginProtection;

namespace TenE0.Core.Auth.Jwt.Commands;

/// <summary>
/// 登录命令处理器。
///
/// 泛型 TUser 让业务方扩展 User 字段后框架直接用扩展类型查询（自动 TPH 同表存储）。
/// TContext 仅约束 DbContext，不强制实现任何接口 — TUser 实体由 TenE0SystemDbContext 基类自动注册到 model。
///
/// <para>
/// <b>#162 登录失败锁定</b>：<paramref name="loginProtector"/> 可选注入（<c>AddTenE0LoginProtection</c>
/// 未启用时为 <c>null</c>）。启用后密码校验前判定是否锁定，失败时计数 + 触发锁定，成功时清零。
/// </para>
/// </summary>
public sealed class LoginCommandHandler<TUser, TContext>(
    IDbContextFactory<TContext> contextFactory,
    IPasswordHasher passwordHasher,
    IJwtTokenService tokenService,
    IErrs errs,
    IAuditLogSink auditSink,
    LoginProtector? loginProtector = null)
    : ICommandHandler<LoginCommand, AuthResult>
    where TUser : TenE0User
    where TContext : DbContext
{
    public async Task<AuthResult> HandleAsync(LoginCommand cmd, CancellationToken ct)
    {
        // #162 登录失败锁定：校验密码前先判定账号是否处于锁定期。
        // 锁定异常（AccountLockedException）由 TenE0ExceptionHandler 映射为 423 + AUTH_LOCKED。
        // #162 review #10：锁定期内的登录尝试单独审计（EventType=Locked），让运维/用户能看到
        // "账号被锁定"事件，区别于普通凭据失败。
        if (loginProtector is not null)
        {
            try
            {
                await loginProtector.EnsureNotLockedAsync(cmd.UserCode, ct);
            }
            catch (AccountLockedException lockEx)
            {
                await auditSink.WriteLoginAsync(new LoginLogEntry
                {
                    UserCode = cmd.UserCode,
                    EventType = "Locked",
                    Success = false,
                    IpAddress = cmd.ClientIp,
                    FailureReason = $"账号锁定至 {lockEx.LockedUntil:O}",
                }, ct);
                throw;
            }
        }

        await using var dc = await contextFactory.CreateDbContextAsync(ct);

        var user = await dc.Set<TUser>().FirstOrDefaultAsync(u => u.UserCode == cmd.UserCode, ct);

        // #97 防 timing attack：用户不存在时也必须跑一次 Verify（走完整 PBKDF2 路径），
        // 否则攻击者通过响应时间差异可枚举有效用户名。短路 `&&` 已被移除。
        var hashToCheck = user?.PasswordHash ?? passwordHasher.DummyHash;
        var verified = passwordHasher.Verify(cmd.Password, hashToCheck);

        if (user is null || !verified)
        {
            // #162 失败计数 + 触发锁定（达到阈值时 EnsureNotLockedAsync 在下次请求即拒）。
            // 即便用户不存在也按 UserCode 计数，防止攻击者用"用户名探测 + 密码枚举"组合绕过。
            if (loginProtector is not null)
            {
                await loginProtector.RecordFailureAsync(cmd.UserCode, ct);
            }

            // #152 登录日志埋点：失败（凭据无效）
            await auditSink.WriteLoginAsync(new LoginLogEntry
            {
                UserCode = cmd.UserCode,
                EventType = "Failed",
                Success = false,
                IpAddress = cmd.ClientIp,
                FailureReason = "用户名或密码错误",
            }, ct);
            errs.Add("用户名或密码错误", code: ErrorCodes.AuthInvalid);
            return null!;
        }

        if (!user.IsActive)
        {
            // #152 登录日志埋点：失败（账号禁用）
            await auditSink.WriteLoginAsync(new LoginLogEntry
            {
                UserCode = cmd.UserCode,
                EventType = "Failed",
                Success = false,
                IpAddress = cmd.ClientIp,
                FailureReason = "账号已被禁用",
            }, ct);
            errs.Add("账号已被禁用", code: ErrorCodes.AuthDisabled);
            return null!;
        }

        var roles = await dc.Set<TenE0UserRole>()
            .Where(ur => ur.UserCode == user.UserCode)
            .Select(ur => ur.RoleCode)
            .ToListAsync(ct);

        // #7: 取每个 role 的当前 version，嵌入 JWT（detached 读 + 显式 select）
        var roleVersions = roles.Count == 0
            ? new Dictionary<string, long>()
            : await dc.Set<TenE0Role>()
                .AsNoTracking()
                .Where(r => roles.Contains(r.Code))
                .ToDictionaryAsync(r => r.Code, r => r.Version, StringComparer.Ordinal, ct);

        // #11/#155: 透传租户 ID 与组织 Id（均可空 → null 时 token 不带对应 claim）
        var tokens = tokenService.Issue(user.UserCode, user.DisplayName, user.UserType, roles, roleVersions, user.TenantId, user.OrgId);

        dc.Set<TenE0RefreshToken>().Add(new TenE0RefreshToken
        {
            TokenHash = tokens.RefreshTokenHash,
            UserCode = user.UserCode,
            ExpiresAt = tokens.RefreshTokenExpiresAt,
            CreatedByIp = cmd.ClientIp,
        });
        await dc.SaveChangesAsync(ct);

        // #162 登录成功 → 清零失败计数（防止"偶尔输错几次 → 累积 → 误锁"）。
        if (loginProtector is not null)
        {
            await loginProtector.RecordSuccessAsync(user.UserCode, ct);
        }

        // #152 登录日志埋点：成功
        await auditSink.WriteLoginAsync(new LoginLogEntry
        {
            UserCode = user.UserCode,
            EventType = "Login",
            Success = true,
            IpAddress = cmd.ClientIp,
            ExpiresAt = tokens.RefreshTokenExpiresAt,
        }, ct);

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
