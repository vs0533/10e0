namespace TenE0.Core.Scheduling;

/// <summary>
/// 标记一个静态定时任务（issue #164）。
///
/// <para>
/// 用法：把 <see cref="IScheduledJob"/> 实现类标上此 attribute，
/// <see cref="StaticJobRegistrar"/> 在启动期扫描程序集，把每个带 <c>[Scheduled]</c> 的类
/// 幂等 upsert 到 <c>TenE0ScheduledJob</c> 表。
/// </para>
///
/// <para>示例：
/// <code>
/// [Scheduled("0 0 9 * * ?", Description = "生成销售日报")]  // 每天 9:00
/// public class DailySalesReportJob : ScheduledJobBase
/// {
///     public override async Task ExecuteAsync(JobContext context, CancellationToken ct)
///     {
///         // ...
///     }
/// }
/// </code>
/// </para>
///
/// <para>
/// <b>Code 推导</b>：默认用类全名（含命名空间），保证全局唯一。如需自定义 Code，
/// 可在 <see cref="Code"/> 显式指定（如避免重构改名导致历史记录断裂）。
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ScheduledAttribute : Attribute
{
    /// <summary>
    /// 构造静态任务标记。
    /// </summary>
    /// <param name="cronExpression">Cron 表达式（6 字段含秒，如 <c>"0 0 9 * * ?"</c>）。</param>
    public ScheduledAttribute(string cronExpression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cronExpression);
        CronExpression = cronExpression;
    }

    /// <summary>Cron 表达式（6 字段含秒）。</summary>
    public string CronExpression { get; }

    /// <summary>
    /// 任务业务编码；为空时由 <see cref="StaticJobRegistrar"/> 用类全名推导。
    /// 显式设置可避免重构改名导致 Code 漂移（Code 是唯一键，改名会被当成新任务）。
    /// </summary>
    public string? Code { get; init; }

    /// <summary>任务展示名称；为空时用类名。</summary>
    public string? Name { get; init; }

    /// <summary>任务描述。</summary>
    public string? Description { get; init; }

    /// <summary>是否启用（默认 true）。</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>最大重试次数（含首次，默认 3）。</summary>
    public int MaxRetries { get; init; } = 3;
}
