using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenE0.Core.Hosting;
using TenE0.Core.Scheduling.Entities;

namespace TenE0.Core.Scheduling;

/// <summary>
/// 静态任务注册器（issue #164）—— 扫描程序集中带 <c>[Scheduled]</c> attribute 的
/// <see cref="IScheduledJob"/> 实现类，启动期幂等 upsert 到 <c>TenE0ScheduledJob</c> 表。
///
/// <para>
/// 实现 <see cref="IDataSeeder"/>，Order=1（在 Outbox SchemaSeeder Order=0 之后，
/// 业务 seeder Order=10+ 之前）。由 <see cref="DatabaseInitializerService{TContext}"/>
/// 在启动期统一调用。
/// </para>
///
/// <para>
/// <b>幂等 upsert</b>：以 <see cref="TenE0ScheduledJob.Code"/> 为键。
/// <list type="bullet">
/// <item>新 Code：插入，<c>Mode = Static</c>。</item>
/// <item>已存在 Code：更新 Cron / Name / JobType / IsEnabled / MaxRetries
///   （让代码改动在重启后生效），但<b>保留</b> LastRunAt / LastRunStatus / NextRunAt
///   （运维历史，不可因代码改动清零）。</item>
/// <item>数据库有但代码已删的 Static 任务：<b>不删</b>（避免误删运维历史），改为禁用
///   （<c>IsEnabled = false</c>），运维可手动清理。</item>
/// </list>
/// </para>
/// </summary>
public sealed class StaticJobRegistrar(
    IOptions<SchedulingOptions> options,
    TimeProvider timeProvider,
    ILogger<StaticJobRegistrar> logger) : IDataSeeder
{
    /// <inheritdoc />
    public int Order => 1;

    /// <inheritdoc />
    public async Task SeedAsync(DbContext context, CancellationToken cancellationToken)
    {
        var assemblies = options.Value.JobAssemblies;
        if (assemblies is null || assemblies.Length == 0)
        {
            return; // 未配置静态任务扫描程序集 → no-op
        }

        var definitions = ScanScheduledJobs(assemblies);
        if (definitions.Count == 0)
        {
            return;
        }

        var existing = await context.Set<TenE0ScheduledJob>()
            .IgnoreQueryFilters() // 系统级注册（全局任务），绕过 Tenant/SoftDelete 过滤器 ——
                                  // seed 阶段在 root provider 跑，解析 ITenantContext (Scoped) 会抛
                                  // "Cannot resolve scoped service from root provider"。
            .Where(j => j.Mode == JobExecutionMode.Static)
            .ToDictionaryAsync(j => j.Code, cancellationToken);

        var now = timeProvider.GetUtcNow();
        var seenCodes = new HashSet<string>(StringComparer.Ordinal);
        var tz = options.Value.TimeZone;

        foreach (var def in definitions)
        {
            seenCodes.Add(def.Code);
            if (existing.TryGetValue(def.Code, out var job))
            {
                // 更新可变属性（让代码改动重启后生效），保留运维历史字段。
                job.Name = def.Name;
                job.CronExpression = def.CronExpression;
                job.JobType = def.JobType;
                job.IsEnabled = def.IsEnabled;
                job.MaxRetries = def.MaxRetries;
                // 若当前 NextRunAt 为空（旧库未初始化），用 Cron 算一个。
                job.NextRunAt ??= CronExtensions.GetNextOccurrence(def.CronExpression, now, tz, def.Code);
                logger.LogInformation("更新静态任务 Code={Code} Cron={Cron}", def.Code, def.CronExpression);
            }
            else
            {
                // 新静态任务。
                var newJob = new TenE0ScheduledJob
                {
                    Code = def.Code,
                    Name = def.Name,
                    CronExpression = def.CronExpression,
                    JobType = def.JobType,
                    IsEnabled = def.IsEnabled,
                    Mode = JobExecutionMode.Static,
                    MaxRetries = def.MaxRetries,
                    RetryInterval = TimeSpan.FromMinutes(1),
                    NextRunAt = CronExtensions.GetNextOccurrence(def.CronExpression, now, tz, def.Code),
                };
                context.Set<TenE0ScheduledJob>().Add(newJob);
                logger.LogInformation("注册静态任务 Code={Code} Cron={Cron}", def.Code, def.CronExpression);
            }
        }

        // 代码已删的 Static 任务 → 禁用（不删，保留运维历史）。
        foreach (var kv in existing)
        {
            if (!seenCodes.Contains(kv.Key) && kv.Value.IsEnabled)
            {
                kv.Value.IsEnabled = false;
                logger.LogInformation("静态任务 Code={Code} 已从代码移除，自动禁用（保留历史，需手动清理）", kv.Key);
            }
        }
    }

    /// <summary>
    /// 扫描程序集中带 <c>[Scheduled]</c> attribute 的 <see cref="IScheduledJob"/> 实现类。
    /// </summary>
    internal static IReadOnlyList<StaticJobDefinition> ScanScheduledJobs(Assembly[] assemblies)
    {
        var list = new List<StaticJobDefinition>();
        foreach (var asm in assemblies)
        {
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException)
            {
                continue; // 部分类型加载失败也跳过，不破坏启动
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface) continue;
                var attr = type.GetCustomAttribute<ScheduledAttribute>();
                if (attr is null) continue;

                if (!typeof(IScheduledJob).IsAssignableFrom(type))
                {
                    throw new InvalidOperationException(
                        $"类型 {type.FullName} 标记了 [Scheduled] 但未实现 IScheduledJob");
                }

                list.Add(new StaticJobDefinition(
                    Code: attr.Code ?? type.FullName ?? type.Name,
                    Name: attr.Name ?? type.Name,
                    CronExpression: attr.CronExpression,
                    JobType: BuildJobTypeName(type),
                    IsEnabled: attr.IsEnabled,
                    MaxRetries: attr.MaxRetries));
            }
        }
        return list;
    }

    /// <summary>
    /// 构造可被 <see cref="System.Type.GetType(string)"/> 加载的类型全名。
    /// 用 <see cref="Type.AssemblyQualifiedName"/> —— 这是 .NET 官方推荐的程序集限定类型名格式
    /// （含 Version/Culture/PublicKeyToken，比手拼 "FullName, SimpleName" 更稳健，
    /// 对强名称程序集也能正确加载；JobExecutor.ResolveJobHandler 用 Type.GetType 还原）。
    /// </summary>
    private static string BuildJobTypeName(Type type)
    {
        // AssemblyQualifiedName 在极少数类型上可能为 null（如泛型开放类型），本场景的 IScheduledJob
        // 都是具体封闭类型，不会为 null；防御性判空保持健壮。
        return type.AssemblyQualifiedName
            ?? throw new InvalidOperationException($"无法为类型 {type.FullName} 生成 AssemblyQualifiedName");
    }
}

/// <summary>
/// 扫描到的静态任务定义（内部传输 DTO）。
/// </summary>
internal sealed record StaticJobDefinition(
    string Code,
    string Name,
    string CronExpression,
    string JobType,
    bool IsEnabled,
    int MaxRetries);
