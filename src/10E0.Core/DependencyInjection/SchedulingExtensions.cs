using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TenE0.Core.Hosting;
using TenE0.Core.Scheduling;

namespace TenE0.Core.DependencyInjection;

/// <summary>
/// Scheduling 模块的 DI 注册扩展（issue #164）。
///
/// <para>
/// 一次性注册以下组件（调用方无需额外步骤）：
/// <list type="bullet">
/// <item><see cref="SchedulerWorker{TContext}"/>：后台扫描到期任务、抢锁、执行。</item>
/// <item><see cref="IScheduler"/> / <see cref="Scheduler{TContext}"/>：管理面 CRUD（Admin API 用）。</item>
/// <item><see cref="JobExecutor{TContext}"/>：执行器（重试 / 历史 / 事件 / NextRunAt）。</item>
/// <item><see cref="IJobLock"/>：集群锁契约（按 <see cref="SchedulingOptions.LockProvider"/> 选型）。</item>
/// <item><see cref="StaticJobRegistrar"/>：扫描 <c>[Scheduled]</c> 注册静态任务（IDataSeeder，Order=1）。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>静态任务 handler 注册</b>：扫描 <c>[Scheduled]</c> 程序集中所有 <see cref="IScheduledJob"/> 实现，
/// 注册为 <c>Scoped</c> —— 让任务能注入 Scoped 服务（DbContext 工厂、命令分发器等）。
/// </para>
/// </summary>
public static class SchedulingExtensions
{
    /// <summary>
    /// 注册 Scheduling 模块。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="jobAssembly">静态任务（<c>[Scheduled]</c>）扫描的程序集；null 则不扫描。</param>
    /// <param name="configure">选项回调；不传用默认值。</param>
    /// <typeparam name="TContext">承载任务表的 EF Core DbContext 类型。</typeparam>
    public static IServiceCollection AddTenE0Scheduling<TContext>(
        this IServiceCollection services,
        Assembly? jobAssembly = null,
        Action<SchedulingOptions>? configure = null)
        where TContext : DbContext
        => services.AddTenE0Scheduling<TContext>(
            jobAssembly is null ? null : [jobAssembly], configure);

    /// <summary>
    /// 注册 Scheduling 模块（多程序集扫描重载）。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="jobAssemblies">静态任务扫描的程序集数组；null 或空则不扫描。</param>
    /// <param name="configure">选项回调；不传用默认值。</param>
    /// <typeparam name="TContext">承载任务表的 EF Core DbContext 类型。</typeparam>
    public static IServiceCollection AddTenE0Scheduling<TContext>(
        this IServiceCollection services,
        Assembly[]? jobAssemblies,
        Action<SchedulingOptions>? configure = null)
        where TContext : DbContext
    {
        // 配置 options：应用 callback + 把 jobAssemblies 写入 options（供白名单与扫描共享）。
        services.AddOptions<SchedulingOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }
        if (jobAssemblies is { Length: > 0 })
        {
            services.Configure<SchedulingOptions>(opt =>
            {
                // 用 Configure 委托补充 JobAssemblies，与业务方 callback 叠加（不覆盖业务方设置）。
                opt.JobAssemblies = jobAssemblies;
                // AllowedAssemblies 未显式配置时回退到 JobAssemblies（默认安全白名单 = 静态任务程序集）。
                opt.AllowedAssemblies ??= jobAssemblies;
            });
        }

        // 后台 Worker：扫描到期任务 → 抢锁 → 执行。
        services.AddHostedService<SchedulerWorker<TContext>>();

        // 管理面（Admin API 用）+ 执行器：Scoped，每请求/每轮一个 scope。
        services.TryAddScoped<IScheduler, Scheduler<TContext>>();
        services.TryAddScoped<JobExecutor<TContext>>();

        // 集群锁契约：按 LockProvider 选型。委托工厂在解析期读 IOptions<SchedulingOptions>。
        services.TryAddSingleton<IJobLock>(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<SchedulingOptions>>().Value;
            switch (opt.LockProvider)
            {
                case JobLockProviderKind.RowLock:
                    {
                        // RowLock 路径：需要 IDbContextFactory<TContext> 创建独立 DbContext 抢锁。
                        // 解析失败 → 保守回退 NoOp，绝不抛异常破坏 Worker 启动。
                        try
                        {
                            var factory = sp.GetRequiredService<IDbContextFactory<TContext>>();
                            return new RowJobLock<TContext>(factory);
                        }
                        catch
                        {
                            return new NoOpJobLock();
                        }
                    }
                case JobLockProviderKind.Distributed:
                    // 预留：本任务未实现 Redis SETNX，回退 NoOp（不抛异常）。
                    return new NoOpJobLock();
                case JobLockProviderKind.None:
                default:
                    return new NoOpJobLock();
            }
        });

        // 静态任务 handler 注册：扫描 [Scheduled] 程序集中的 IScheduledJob 实现，注册为 Scoped。
        if (jobAssemblies is { Length: > 0 })
        {
            foreach (var asm in jobAssemblies)
            {
                RegisterScheduledJobHandlers(services, asm);
            }
        }

        // 静态任务注册器（IDataSeeder，Order=1）：扫描 [Scheduled] 幂等 upsert 到表。
        // TryAddEnumerable 允许业务方在特殊场景下 Replace 为自己的 seeder。
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDataSeeder, StaticJobRegistrar>());

        return services;
    }

    /// <summary>
    /// 扫描程序集中所有 <see cref="IScheduledJob"/> 实现，注册为 Scoped。
    /// （无论是否标 <c>[Scheduled]</c>，只要实现接口就注册 —— 动态任务反射解析也走 DI。）
    /// </summary>
    private static void RegisterScheduledJobHandlers(IServiceCollection services, Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException)
        {
            return;
        }

        foreach (var type in types)
        {
            if (type.IsAbstract || type.IsInterface) continue;
            if (typeof(IScheduledJob).IsAssignableFrom(type))
            {
                // 注册具体类型（让 JobExecutor.ResolveJobHandler 能 GetRequiredService(type)）。
                services.TryAddScoped(type);
            }
        }
    }
}
