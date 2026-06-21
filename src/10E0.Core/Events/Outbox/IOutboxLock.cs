namespace TenE0.Core.Events.Outbox;

/// <summary>
/// Outbox 行级锁抽象 — 多实例部署下避免 Relay 竞争同一行（已知风险 #1）。
///
/// <para>
/// 契约语义：
/// - <see cref="TryAcquireAsync"/> 返回 <c>true</c> 表示本实例在租约期内独占地持有了该消息；
///   返回 <c>false</c> 表示该消息已被另一实例持有或尚未到期，本实例本轮跳过（不应增加 <c>AttemptCount</c>）。
/// - <see cref="ReleaseAsync"/> 由本实例主动归还租约；实现层必须按 (messageId, instanceId) 校验所有权，
///   避免误释放其他实例的锁（典型实现：仅当 <c>LockedByInstance == instanceId</c> 时清空）。
/// - 租约到期（<c>ExpiresOn &lt;= now</c>）即视为锁自动失效，无需调用 Release；
///   另一实例下轮 TryAcquire 即可重新拾取（典型实现：UPDATE ... WHERE LockedUntil &lt; now）。
/// </para>
///
/// <para>
/// 切到真实数据库 provider 时，仅需替换 DI 注册为本接口的具体实现：
/// <code>
/// services.Replace(ServiceDescriptor.Singleton&lt;IOutboxLock, SqlServerOutboxLock&gt;());
/// services.Replace(ServiceDescriptor.Singleton&lt;IOutboxLock, PostgresOutboxLock&gt;());
/// </code>
/// </para>
/// </summary>
public interface IOutboxLock
{
    /// <summary>
    /// 尝试为指定消息获取一把行级锁。
    /// </summary>
    /// <param name="messageId">目标 OutboxMessage 主键（与 <see cref="OutboxMessage.Id"/> 类型一致：string）。</param>
    /// <param name="instanceId">当前实例的唯一标识（通常来自 <c>OutboxRelayOptions.LockInstanceId</c>）。</param>
    /// <param name="lease">期望的租约时长；实现层应将 <c>LockedUntil</c> 设为 now + lease。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// <c>true</c> 表示本实例在租约期内独占地持有了该消息；
    /// <c>false</c> 表示该消息已被另一实例持有或尚未到期，本实例本轮跳过（不应增加 <c>AttemptCount</c>）。
    /// </returns>
    Task<bool> TryAcquireAsync(
        string messageId,
        string instanceId,
        TimeSpan lease,
        CancellationToken cancellationToken);

    /// <summary>
    /// 释放由本实例先前获取的行级锁。
    /// </summary>
    /// <param name="messageId">目标 OutboxMessage 主键。</param>
    /// <param name="instanceId">持有锁的实例 ID（必须与获取时一致，否则实现层应拒绝释放）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>无返回；本方法幂等，不存在即不抛异常。</returns>
    Task ReleaseAsync(string messageId, string instanceId, CancellationToken cancellationToken);
}
