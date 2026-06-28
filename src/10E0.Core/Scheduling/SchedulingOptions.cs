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
    /// 默认 5 分钟，防止僵死任务占锁。长任务应同时调大本值与 <see cref="LockLeaseDuration"/>
    /// （必须 <c>LockLeaseDuration ≥ JobTimeout</c>，启动期校验，否则崩溃后会双重执行）。
    /// </summary>
    public TimeSpan JobTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 任务执行锁的租约时长。默认 2 分钟。
    /// <para>
    /// <b>语义与约束</b>：租约 = 单个任务<b>单次执行</b>的最长独占窗口（SchedulerWorker
    /// 对每个 job 单独 TryAcquire/Release，不是整批持锁）。需满足 <c>LockLeaseDuration ≥ JobTimeout</c>：
    /// </para>
    /// <list type="bullet">
    /// <item>租约 &lt; JobTimeout → 任务还在跑但锁已过期，另一实例会重复拾取（双重执行）。</item>
    /// <item>租约 ≫ JobTimeout → 实例崩溃后接管延迟变长（需等租约到期）。</item>
    /// </list>
    /// <para>
    /// 默认 5 分钟（= 默认 JobTimeout，满足校验约束；崩溃后另一实例接管延迟 5 分钟，可接受）。
    /// 长任务应同时调大 JobTimeout 与本值。改 JobTimeout 时务必同步检查
    /// <see cref="LockLeaseDuration"/>（SchedulingExtensions 启动期 ValidateOnStart 会拦非法配置）。
    /// </para>
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
