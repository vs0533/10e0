using Microsoft.EntityFrameworkCore;
using TenE0.Core.Scheduling.Entities;

namespace TenE0.Core.Scheduling;

/// <summary>
/// Scheduling 模块的 EF Core 表映射（issue #164）。
///
/// <para>
/// 列长常量集中管理（仿 <c>OutboxModelBuilderExtensions</c>），后续若新增 Schema 升级 seeder
/// 必须从此处读取，避免 entity 改 MaxLength 后 seeder ALTER 出旧长度导致漂移。
/// </para>
/// </summary>
public static class SchedulingModelBuilderExtensions
{
    /// <summary>任务 Code 列长（业务编码唯一）。</summary>
    public const int CodeMaxLength = 128;

    /// <summary>任务 Name 列长。</summary>
    public const int NameMaxLength = 256;

    /// <summary>Cron 表达式列长。</summary>
    public const int CronExpressionMaxLength = 128;

    /// <summary>JobType（类型全名）列长。</summary>
    public const int JobTypeMaxLength = 512;

    /// <summary>LastRunStatus 列长。</summary>
    public const int LastRunStatusMaxLength = 32;

    /// <summary>ErrorMessage 列长。</summary>
    public const int ErrorMessageMaxLength = 2048;

    /// <summary>LockedByInstance / InstanceId 列长（与 Outbox <c>LockedByInstanceMaxLength</c> 一致）。</summary>
    public const int InstanceIdMaxLength = 128;

    /// <summary>TenantId 列长。</summary>
    public const int TenantIdMaxLength = 64;

    /// <summary>
    /// 配置 Scheduling 模块表（由 <c>TenE0SystemDbContext</c> 自动调用）。
    /// </summary>
    public static ModelBuilder ConfigureTenE0SchedulingTables(this ModelBuilder mb)
    {
        mb.Entity<TenE0ScheduledJob>(b =>
        {
            b.Property(j => j.Code).HasMaxLength(CodeMaxLength).IsRequired();
            b.Property(j => j.Name).HasMaxLength(NameMaxLength).IsRequired();
            b.Property(j => j.CronExpression).HasMaxLength(CronExpressionMaxLength).IsRequired();
            b.Property(j => j.JobType).HasMaxLength(JobTypeMaxLength).IsRequired();
            b.Property(j => j.LastRunStatus).HasMaxLength(LastRunStatusMaxLength);
            b.Property(j => j.LockedByInstance).HasMaxLength(InstanceIdMaxLength);
            b.Property(j => j.TenantId).HasMaxLength(TenantIdMaxLength).IsRequired();

            // Code 全局唯一（跨租户）—— Cron 调度通常全局共享，租户隔离由业务任务内部处理。
            b.HasIndex(j => j.Code).IsUnique();

            // SchedulerWorker pick: WHERE IsEnabled AND NextRunAt <= now
            // 把 IsEnabled 作前导列，跳过禁用任务；NextRunAt 用于范围扫描到期任务。
            b.HasIndex(j => new { j.IsEnabled, j.NextRunAt });

            // RowJobLock 抢锁路径：跳过已锁行时按 (LockedUntil) 走索引扫描
            b.HasIndex(j => j.LockedUntil);
        });

        mb.Entity<TenE0JobExecution>(b =>
        {
            b.Property(e => e.JobId).HasMaxLength(InstanceIdMaxLength).IsRequired();
            b.Property(e => e.Status).HasMaxLength(LastRunStatusMaxLength).IsRequired();
            b.Property(e => e.ErrorMessage).HasMaxLength(ErrorMessageMaxLength);
            b.Property(e => e.InstanceId).HasMaxLength(InstanceIdMaxLength);

            // 按任务查历史 + 按时间倒序（常见运维查询）
            b.HasIndex(e => new { e.JobId, e.StartedAt });
        });

        return mb;
    }
}
