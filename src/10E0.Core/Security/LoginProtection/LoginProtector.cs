using Microsoft.Extensions.Options;

namespace TenE0.Core.Security.LoginProtection;

/// <summary>
/// 登录防刷核心逻辑：失败计数 + 阈值锁定 + 自动解锁（issue #162）。
///
/// <para>
/// <b>三步契约</b>（在 <c>LoginCommandHandler</c> 密码校验前后调用）：
/// <code>
/// var state = await protector.EnsureNotLockedAsync(userCode, ct);  // 锁定期内直接拒
/// if (!passwordValid)
///     await protector.RecordFailureAsync(userCode, ct);            // 计数 + 触发锁定
/// else
///     await protector.RecordSuccessAsync(userCode, ct);            // 清零
/// </code>
/// </para>
///
/// <para>
/// 锁定判定 + 计数阈值都从 <see cref="LoginProtectionOptions"/> 读取，让运维可通过
/// <c>ISystemParameterStore</c> 动态覆盖（#153 协同点）。
/// </para>
/// </summary>
public sealed class LoginProtector
{
    private readonly ILoginAttemptStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly IOptions<LoginProtectionOptions> _options;

    public LoginProtector(
        ILoginAttemptStore store,
        TimeProvider timeProvider,
        IOptions<LoginProtectionOptions> options)
    {
        _store = store;
        _timeProvider = timeProvider;
        _options = options;
    }

    /// <summary>
    /// 判定账号是否处于锁定期。锁定期内抛 <see cref="AccountLockedException"/>。
    /// 未锁返回当前累计失败次数（供审计日志使用）。
    /// </summary>
    public async Task<LoginAttemptState> EnsureNotLockedAsync(string userCode, CancellationToken ct = default)
    {
        var opts = _options.Value;
        if (!opts.LockoutEnabled) return new LoginAttemptState(0, null);

        var state = await _store.GetAsync(userCode, ct);
        if (state.IsLocked(_timeProvider))
        {
            throw new AccountLockedException(userCode, state.LockedUntil!.Value);
        }
        return state;
    }

    /// <summary>
    /// 记录一次失败。达到阈值时自动写入锁定截止时间。
    /// 返回失败后的最新状态（含是否触发锁定）—— 调用方据此决定错误码 / 日志内容。
    /// </summary>
    public async Task<LoginAttemptState> RecordFailureAsync(string userCode, CancellationToken ct = default)
    {
        var opts = _options.Value;
        if (!opts.LockoutEnabled) return new LoginAttemptState(0, null);

        return await _store.RecordFailureAsync(
            userCode,
            opts.MaxFailedAttempts,
            opts.SlidingWindow,
            opts.LockoutDuration,
            _timeProvider,
            ct);
    }

    /// <summary>登录成功 → 清零计数 + 移除锁定。</summary>
    public Task RecordSuccessAsync(string userCode, CancellationToken ct = default)
        => _store.RecordSuccessAsync(userCode, ct);

    /// <summary>主动重置（管理员解锁 / 用户验证身份后解锁）。</summary>
    public Task ResetAsync(string userCode, CancellationToken ct = default)
        => _store.ResetAsync(userCode, ct);
}

/// <summary>
/// 账号锁定异常。由 <see cref="LoginProtector.EnsureNotLockedAsync"/> 抛出，
/// 被 <c>TenE0ExceptionHandler</c> 映射为 423 Locked + <c>AUTH_LOCKED</c>。
/// </summary>
public sealed class AccountLockedException : Exception
{
    /// <summary>被锁定的用户标识。</summary>
    public string UserCode { get; }

    /// <summary>锁定截止时间。客户端可据此提示"剩余 N 分钟自动解锁"。</summary>
    public DateTimeOffset LockedUntil { get; }

    public AccountLockedException(string userCode, DateTimeOffset lockedUntil)
        : base($"账号已被锁定，至 {lockedUntil:O} 自动解锁。")
    {
        UserCode = userCode;
        LockedUntil = lockedUntil;
    }
}
