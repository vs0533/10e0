namespace TenE0.Core.Security.LoginProtection;

/// <summary>
/// 登录尝试计数存储抽象（issue #162）。
///
/// <para>
/// 把"失败计数 / 锁定状态"从核心逻辑解耦到存储后端。默认 <see cref="InMemoryLoginAttemptStore"/>
/// （单实例足够）；多副本部署提供 <c>DistributedLoginAttemptStore</c>（Redis，本 issue 不强制实现，
/// 业务方按 <c>IMultiLevelCache</c> / Outbox 锁的 DI Replace 风格自行接入）。
/// </para>
///
/// <para><b>并发安全</b>：实现必须保证 <see cref="RecordFailureAsync"/> 的 read-modify-write 原子性
/// （InMemory 用 <c>lock</c> / <c>ConcurrentDictionary</c>；分布式用 Redis <c>INCR</c>）。
/// 否则高并发撞库会少计数、锁不住。</para>
/// </summary>
public interface ILoginAttemptStore
{
    /// <summary>读取当前累计失败次数 + 锁定截止时间（未锁 <c>null</c>）。</summary>
    Task<LoginAttemptState> GetAsync(string userCode, CancellationToken ct = default);

    /// <summary>
    /// 记录一次失败：在滑动窗口内累计计数；达到阈值时写入锁定截止时间。
    /// 返回失败后的最新状态（含是否触发锁定）。
    /// </summary>
    Task<LoginAttemptState> RecordFailureAsync(
        string userCode,
        int maxFailedAttempts,
        TimeSpan slidingWindow,
        TimeSpan lockoutDuration,
        TimeProvider timeProvider,
        CancellationToken ct = default);

    /// <summary>登录成功：清零计数 + 清除锁定。让用户从干净状态起步。</summary>
    Task RecordSuccessAsync(string userCode, CancellationToken ct = default);

    /// <summary>主动解锁（管理员重置 / 用户验证身份后解锁）。</summary>
    Task ResetAsync(string userCode, CancellationToken ct = default);
}

/// <summary>
/// 登录尝试状态（不可变快照）。
/// </summary>
/// <param name="FailedCount">滑动窗口内累计失败次数。</param>
/// <param name="LockedUntil">锁定截止时间；<c>null</c> 表示未锁。已过期视为未锁（调用方判定）。</param>
public sealed record LoginAttemptState(int FailedCount, DateTimeOffset? LockedUntil)
{
    /// <summary>是否被锁定（<see cref="LockedUntil"/> 非空且未过期）。</summary>
    public bool IsLocked(TimeProvider timeProvider) =>
        LockedUntil is { } until && until > timeProvider.GetUtcNow();
}
