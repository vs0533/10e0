namespace TenE0.Core.Scheduling;

/// <summary>
/// 定时任务的集群执行锁抽象（issue #164）—— 多实例部署下避免同一任务被多实例同时执行。
///
/// <para>
/// 复用 Outbox <c>IOutboxLock</c> 的设计模式（#80/#81）：
/// <list type="bullet">
/// <item><see cref="TryAcquireAsync"/> 返回 <c>true</c> 表示本实例在租约期内独占地持有该任务的执行权；
///   返回 <c>false</c> 表示该任务已被另一实例执行（或租约未到），本实例本轮跳过。</item>
/// <item><see cref="ReleaseAsync"/> 由本实例主动归还；实现层必须按 (jobCode, instanceId) 校验所有权，
///   避免误释放其他实例的锁。</item>
/// <item>租约到期即视为锁自动失效，无需调 Release；另一实例下轮 TryAcquire 即可重新拾取。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>与 Outbox 锁的区别</b>：Outbox 锁以单条消息为单位（messageId），任务锁以任务为单位（jobCode）。
/// 因此任务锁额外提供 <see cref="IsRunningAsync"/> —— 用于「手动触发」场景判断任务是否正被某实例执行，
/// 避免手动触发与定时触发重叠。
/// </para>
///
/// <para>
/// <b>已知实现</b>：
/// <list type="bullet">
/// <item><see cref="NoOpJobLock"/>：无锁，0/1 实例部署默认。</item>
/// <item><see cref="RowJobLock{TContext}"/>：数据库行级锁（LINQ + SQL 双路径），多实例部署。</item>
/// </list>
/// </para>
/// </summary>
public interface IJobLock
{
    /// <summary>
    /// 尝试为指定任务获取执行锁。
    /// </summary>
    /// <param name="jobCode">任务业务编码（<see cref="Entities.TenE0ScheduledJob.Code"/>）。</param>
    /// <param name="instanceId">当前实例的唯一标识（来自 <c>SchedulingOptions.LockInstanceId</c>）。</param>
    /// <param name="lease">租约时长；实现层应把 <c>LockedUntil</c> 设为 now + lease。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// <c>true</c> 本实例独占持有；<c>false</c> 已被他人持有且租约未到，本轮跳过。
    /// </returns>
    Task<bool> TryAcquireAsync(
        string jobCode,
        string instanceId,
        TimeSpan lease,
        CancellationToken cancellationToken);

    /// <summary>
    /// 释放由本实例先前获取的执行锁。幂等，不存在即不抛异常。
    /// </summary>
    /// <param name="jobCode">任务业务编码。</param>
    /// <param name="instanceId">持有锁的实例 ID（必须与获取时一致，否则实现层应拒绝释放）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task ReleaseAsync(string jobCode, string instanceId, CancellationToken cancellationToken);

    /// <summary>
    /// 判断指定任务是否正被某实例执行（锁未被释放且租约未到）。
    /// </summary>
    /// <param name="jobCode">任务业务编码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns><c>true</c> 正在执行；<c>false</c> 空闲。</returns>
    Task<bool> IsRunningAsync(string jobCode, CancellationToken cancellationToken);
}
