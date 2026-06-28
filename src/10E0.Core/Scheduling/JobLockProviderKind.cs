namespace TenE0.Core.Scheduling;

/// <summary>
/// 任务执行锁 provider 选择枚举（issue #164）—— 由 <see cref="SchedulingOptions.LockProvider"/>
/// 配置驱动，决定 DI 容器为 <see cref="IJobLock"/> 注入哪种实现。
///
/// <para>
/// 命名与设计对齐 Outbox 的 <c>OutboxLockProviderKind</c>（None/RowLock/Distributed/Leader），
/// 但本模块当前只实现 <see cref="None"/> + <see cref="RowLock"/>；<see cref="Distributed"/>
/// 预留为后续基于 Redis SETNX 的实现（接口 <see cref="IJobLock"/> 已就绪）。
/// </para>
///
/// <para>取值约定：枚举 int 值仅追加，禁止重排已有值（向后兼容老配置）。</para>
/// </summary>
public enum JobLockProviderKind
{
    /// <summary>无锁，等价于 <see cref="NoOpJobLock"/>（0/1 实例部署默认）。</summary>
    None = 0,

    /// <summary>数据库行级锁（<see cref="RowJobLock{TContext}"/>），多实例部署。</summary>
    RowLock = 1,

    /// <summary>
    /// 分布式锁（Redis SETNX 等）。本任务未实现，配置时回退 <see cref="None"/>，避免运行时 NRE。
    /// </summary>
    Distributed = 2,
}
