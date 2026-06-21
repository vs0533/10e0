namespace TenE0.Core.Events.Outbox;

/// <summary>
/// Outbox 行级锁 provider 选择枚举 — 由 <see cref="OutboxRelayOptions.LockProvider"/> 配置，
/// 决定 DI 容器为 <see cref="IOutboxLock"/> 注入哪种实现。
///
/// <para>
/// 取值：
/// <list type="bullet">
/// <item><see cref="None"/>：默认值，与 <see cref="NoOpOutboxLock"/> 等价。0/1 实例部署零感知。</item>
/// <item><see cref="RowLock"/>：基于数据库行级锁（SQL Server UPDLOCK,READPAST / PostgreSQL FOR UPDATE SKIP LOCKED）。
/// 适用于同库多实例 Relay 并发场景。</item>
/// <item><see cref="Distributed"/>：基于外部分布式锁（Redis / SqlServer sp_getapplock 等）。本任务未实现，
/// 配置时回退 <see cref="None"/>，避免运行时 NRE。</item>
/// </list>
/// </para>
///
/// <para>
/// 命名规范：枚举后缀 <c>Kind</c> 以避免与同名静态选择器类（<c>OutboxLockProvider</c>）冲突。
/// </para>
/// </summary>
public enum OutboxLockProviderKind
{
    /// <summary>无锁，等价于 <see cref="NoOpOutboxLock"/>。</summary>
    None = 0,

    /// <summary>数据库行级锁 provider（SQL Server / PostgreSQL）。</summary>
    RowLock = 1,

    /// <summary>分布式锁 provider（Redis 等）。本任务未实现，回退 <see cref="None"/>。</summary>
    Distributed = 2,
}
