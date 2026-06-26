using System.Reflection;

namespace TenE0.Core.Scheduling;

/// <summary>
/// Scheduling 模块的运行参数（issue #164）。
///
/// <para>
/// 由 <c>AddTenE0Scheduling&lt;TContext&gt;(jobAssembly, configure)</c> 注册到
/// <c>IOptions&lt;SchedulingOptions&gt;</c>，运行时由
/// <see cref="SchedulerWorker{TContext}"/> / <see cref="JobExecutor{TContext}"/> 读取。
/// </para>
/// </summary>
public sealed class SchedulingOptions
{
    /// <summary>
    /// 扫描间隔：<see cref="SchedulerWorker{TContext}"/> 每隔多久扫一次到期任务。
    /// 默认 30s。短间隔响应更快但 DB 压力更大；测试可设短（如 100ms）验证调度。
    /// </summary>
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Cron 表达式解析时使用的时区；默认 <see cref="TimeZoneInfo.Utc"/> 避免夏令时坑。
    /// 业务任务通常用 UTC 计算，需本地时区时显式配置。
    /// </summary>
    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Utc;

    /// <summary>
    /// 单个任务执行的硬超时。超过即取消 CancellationToken 并标记 Timeout。
    /// 默认 30 分钟，防止僵死任务占锁。具体任务可在 <c>[Scheduled]</c> 上覆盖（后续）。
    /// </summary>
    public TimeSpan JobTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 任务执行锁的租约时长。默认 5 分钟 —— 远长于单次任务执行的预期耗时，
    /// 但短到一旦实例崩溃，另一实例能在可接受时间内接管（与 Outbox 的 30s 相比更长，
    /// 因为任务执行通常比单条消息投递久）。
    /// </summary>
    public TimeSpan LockLeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 锁 provider 选择：决定 <c>IJobLock</c> 注入哪种实现。
    /// 默认 <see cref="JobLockProviderKind.None"/>（0/1 实例部署零感知）。
    /// 多实例部署配 <see cref="JobLockProviderKind.RowLock"/>。
    /// </summary>
    public JobLockProviderKind LockProvider { get; set; } = JobLockProviderKind.None;

    /// <summary>
    /// 当前实例的唯一标识；同时作为 <c>LockedByInstance</c> 写入行。
    /// 默认 <c>Environment.MachineName + Guid.NewGuid()</c>：
    /// 同机多实例（容器/端口隔离）天然不冲突；跨机部署天然不冲突。
    /// </summary>
    public string LockInstanceId { get; set; } =
        $"{Environment.MachineName}-{Guid.NewGuid():N}";

    /// <summary>
    /// 动态任务的 <c>JobType</c> 反射加载白名单程序集。
    /// 动态任务创建时，<c>JobType</c> 所在程序集必须在此列表内，否则拒绝（防任意代码执行）。
    /// 为空时回退到 <see cref="JobAssemblies"/>（静态任务扫描用的程序集）。
    /// </summary>
    public Assembly[]? AllowedAssemblies { get; set; }

    /// <summary>
    /// 静态任务（<c>[Scheduled]</c> attribute）扫描用的程序集列表。
    /// 由 <see cref="StaticJobRegistrar"/> 启动期扫描注册。通常由 DI 扩展传入。
    /// </summary>
    public Assembly[] JobAssemblies { get; set; } = [];
}
