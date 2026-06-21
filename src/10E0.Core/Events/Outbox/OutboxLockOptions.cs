namespace TenE0.Core.Events.Outbox;

/// <summary>
/// Outbox 行级锁运行参数 — 由 Relay/锁实现层共享读取。
///
/// <para>
/// 注意：<see cref="LockLeaseDuration"/> 与 <see cref="LockInstanceId"/> 的权威来源
/// 已迁移到 <see cref="OutboxRelayOptions"/>（避免 Relay 与锁实现层各自定义一套产生漂移）。
/// 本类型保留是为向后兼容老配置代码：构造时若不显式赋值，则使用与
/// <see cref="OutboxRelayOptions"/> 完全一致的默认值（30s 租约 + 空字符串实例 ID —
/// 业务方应通过 <see cref="OutboxRelayOptions"/> 配置；锁实现层统一从
/// <c>IOptions&lt;OutboxRelayOptions&gt;</c> 读取锁相关参数）。
/// </para>
///
/// <para>
/// <b>为什么不直接 <c>new OutboxRelayOptions()</c> 派生默认值？</b>
/// 那会让每个 <c>new OutboxLockOptions()</c> 都生成新的随机 <c>LockInstanceId</c>，
/// 破坏"权威来源"语义；本类作为轻量 POCO，默认值必须确定且可预测。
/// </para>
/// </summary>
public sealed class OutboxLockOptions
{
    /// <summary>
    /// 锁租约时长（TryAcquire 时写入 <c>LockedUntil</c> 的偏移量）。
    /// 默认 30s，与 <see cref="OutboxRelayOptions.LockLeaseDuration"/> 一致。
    /// </summary>
    public TimeSpan LockLeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 当前实例的唯一标识；同时作为 <c>LockedByInstance</c> 写入行。
    /// 默认空串 — 业务方必须显式赋值（建议走 <see cref="OutboxRelayOptions.LockInstanceId"/>）。
    /// </summary>
    public string LockInstanceId { get; set; } = string.Empty;
}
