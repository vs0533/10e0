namespace TenE0.Core.Scheduling;

/// <summary>
/// 无操作（No-Op）的 <see cref="IJobLock"/> 实现（issue #164）—— 0/1 实例部署的默认策略。
///
/// <para>
/// 语义：
/// <list type="bullet">
/// <item><see cref="TryAcquireAsync"/> 始终返回 <c>true</c>（让单实例正常执行所有到期任务）。</item>
/// <item><see cref="ReleaseAsync"/> 是 no-op。</item>
/// <item><see cref="IsRunningAsync"/> 始终返回 <c>false</c>（单实例下不会与本实例重叠）。</item>
/// </list>
/// </para>
///
/// <para>
/// 设计上无依赖（无 TimeProvider / DbContext），让单测能直接 <c>new NoOpJobLock()</c>；
/// 也让 0/1 实例部署可以零配置获得默认锁行为。多实例部署请配置
/// <c>SchedulingOptions.LockProvider = JobLockProviderKind.RowLock</c> 切到 <see cref="RowJobLock{TContext}"/>。
/// </para>
/// </summary>
public sealed class NoOpJobLock : IJobLock
{
    /// <inheritdoc />
    public Task<bool> TryAcquireAsync(
        string jobCode,
        string instanceId,
        TimeSpan lease,
        CancellationToken cancellationToken)
        => Task.FromResult(true);

    /// <inheritdoc />
    public Task ReleaseAsync(string jobCode, string instanceId, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<bool> IsRunningAsync(string jobCode, CancellationToken cancellationToken)
        => Task.FromResult(false);
}
