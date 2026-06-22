using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TenE0.Core.Caching;

namespace TenE0.Core.Events.Outbox;

/// <summary>
/// 无操作（No-Op）的 <see cref="IOutboxLock"/> 实现 — 0/1 实例部署场景的回退策略。
///
/// <para>
/// 语义：
/// - <see cref="TryAcquireAsync"/> 始终返回 <c>true</c>（让 Relay 在所有候选行上都走投递路径，保持旧行为）。
/// - <see cref="ReleaseAsync"/> 是 no-op，绝不因未持有锁而抛异常。
/// </para>
///
/// <para>
/// 切到真实数据库 provider 时，仅需在 DI 容器里替换为本接口的具体实现：
/// <code>
/// services.Replace(ServiceDescriptor.Singleton&lt;IOutboxLock, SqlServerOutboxLock&gt;());
/// services.Replace(ServiceDescriptor.Singleton&lt;IOutboxLock, PostgresOutboxLock&gt;());
/// </code>
/// </para>
///
/// <para>
/// 设计上保持无依赖（无 TimeProvider / IOptions / ILogger），让单测能直接 <c>new NoOpOutboxLock()</c>；
/// 也让 0/1 实例部署可以零配置就获得默认锁行为。
/// </para>
/// </summary>
public sealed class NoOpOutboxLock : IOutboxLock
{
    /// <inheritdoc />
    public Task<bool> TryAcquireAsync(
        string messageId,
        string instanceId,
        TimeSpan lease,
        CancellationToken cancellationToken)
        => Task.FromResult(true);

    /// <inheritdoc />
    public Task ReleaseAsync(string messageId, string instanceId, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

/// <summary>
/// Outbox 行级锁相关服务的 DI 注册扩展。
///
/// <para>
/// 默认注册：
/// - <see cref="IOutboxLock"/> → <see cref="NoOpOutboxLock"/>（0/1 实例部署零感知）
/// - <see cref="OutboxLockOptions"/> → 单例（保留以兼容老配置代码；真实参数走 <see cref="OutboxRelayOptions"/>）
/// </para>
///
/// <para>
/// 多实例部署时调用 <see cref="AddOutboxLocking{TContext}"/>（泛型版本），DI 层会按
/// <see cref="OutboxRelayOptions.LockProvider"/> + 底层 EF Core <c>ProviderName</c> 字符串
/// 探测（switch 表达式）注册具体 provider 实现（<see cref="SqlServerOutboxLock{T}"/> /
/// <see cref="PostgresOutboxLock{T}"/>）。探测逻辑与 <see cref="OutboxSchemaSeeder"/>
/// 同款（按命名匹配 SqlServer / Npgsql / Postgres，其余回退 NoOp）。
/// </para>
///
/// <para>
/// Relay 编排代码零改动 — 始终只拿 <see cref="IOutboxLock"/>，选型在 DI 解析期完成。
/// </para>
/// </summary>
public static class OutboxLockingServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Outbox 行级锁默认实现 + 配置 <see cref="OutboxLockOptions"/>。
    /// 返回同一 <see cref="IServiceCollection"/> 以便链式调用。
    ///
    /// <para>
    /// 0/1 实例部署零感知路径：仅注册 <see cref="NoOpOutboxLock"/>，不依赖 <c>IDbContextFactory</c>。
    /// 多实例部署请改用 <see cref="AddOutboxLocking{TContext}"/>。
    /// </para>
    /// </summary>
    public static IServiceCollection AddOutboxLocking(this IServiceCollection services)
    {
        // 默认实现：NoOpOutboxLock。无外部依赖，可直接单例。
        services.TryAddSingleton<IOutboxLock, NoOpOutboxLock>();

        // 保留 OutboxLockOptions 注册以兼容老配置代码。运行时参数的真实权威来源仍是 OutboxRelayOptions。
        services.TryAddSingleton(new OutboxLockOptions());

        return services;
    }

    /// <summary>
    /// 按 <see cref="OutboxRelayOptions.LockProvider"/> + 底层 EF Core <c>ProviderName</c> 字符串
    /// 探测注册具体 <see cref="IOutboxLock"/> 实现。
    ///
    /// <para>
    /// 选型规则：
    /// <list type="number">
    /// <item><see cref="OutboxLockProviderKind.None"/>（默认）→ <see cref="NoOpOutboxLock"/>（0/1 实例部署零感知）</item>
    /// <item><see cref="OutboxLockProviderKind.Distributed"/> → <see cref="DistributedOutboxLock"/>（基于 <see cref="IMultiLevelCache"/> L2 的应用层分布式锁）</item>
    /// <item><see cref="OutboxLockProviderKind.Leader"/> → <see cref="LeaderElector"/>（Leader Election 模式：全局只一个 Relay 实例承担投递）</item>
    /// <item><see cref="OutboxLockProviderKind.RowLock"/> + <c>ProviderName</c> 含 <c>SqlServer</c> → <see cref="SqlServerOutboxLock{T}"/></item>
    /// <item><see cref="OutboxLockProviderKind.RowLock"/> + <c>ProviderName</c> 含 <c>Npgsql</c> → <see cref="PostgresOutboxLock{T}"/></item>
    /// <item><see cref="OutboxLockProviderKind.RowLock"/> + 其他 <c>ProviderName</c>（InMemory / 未知）→ <see cref="NoOpOutboxLock"/>（保守回退，绝不抛异常）</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>为什么用 switch 表达式而不用 <see cref="OutboxLockProvider.ResolveLockType"/>？</b>
    /// 本步目标是把选型逻辑直接落进 DI 扩展方法里 — 沿用 <see cref="OutboxSchemaSeeder"/>
    /// "用 EF Core ProviderName 字符串命名匹配" 的 switch 表达式风格，保持 Outbox 基础设施
    /// 各模块探测方式一致。后续步骤若需统一抽象可重构。
    /// </para>
    ///
    /// <para>
    /// <typeparamref name="TContext"/> 约束沿用 <see cref="OutboxAdminService{TContext}"/> +
    /// <see cref="OutboxRelayService{TContext}"/> 已有的泛型签名惯例 — 调用方一次
    /// <c>AddTenE0DomainEvents&lt;MyDbContext&gt;()</c> 即可获得完整 Outbox 基础设施
    /// （后台投递 + 运维管理 + 行级锁）。
    /// </para>
    /// </summary>
    /// <typeparam name="TContext">承载 <c>OutboxMessage</c> 表的 EF Core <see cref="DbContext"/> 类型。</typeparam>
    /// <param name="services">DI 容器。</param>
    /// <returns>同一 <see cref="IServiceCollection"/> 以便链式调用。</returns>
    public static IServiceCollection AddOutboxLocking<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        // 保留 OutboxLockOptions 注册以兼容老配置代码（与无参 AddOutboxLocking 行为一致）。
        services.TryAddSingleton(new OutboxLockOptions());

        // LockProvider 选型 — 通过配置回调在 DI 解析期读取（避免启动期强依赖配置源）。
        // 用 AddSingleton 委托工厂：解析时由 DI 容器注入 IOptions<OutboxRelayOptions> + IDbContextFactory<TContext>。
        //
        // <para>
        // <b>配置热重载语义</b>：<see cref="IOutboxLock"/> 注册为单例，意味着委托工厂只在首次解析时跑一次。
        // 首次解析期 <see cref="OutboxRelayOptions.LockProvider"/> 的值决定最终注入的 lock 实现类型；
        // 后续通过 <c>IOptionsMonitor&lt;OutboxRelayOptions&gt;</c> 改动 <c>LockProvider</c> 不会重新探测。
        // 多实例部署从 RowLock 切到 None（或反之）需重启进程。这是与 #80 Relay 单例行为的同款简化。
        // </para>
        services.TryAddSingleton<IOutboxLock>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<OutboxRelayOptions>>().Value;

            // 非 RowLock 路径：按枚举分派到对应 provider 实现（feature #82 扩展 4/6）。
            // RowLock 路径走下方基于 EF Core ProviderName 的命名匹配。
            // 解析期抛异常必须被 try/catch 兜底回退 NoOp，绝不破坏 Relay 启动。
            switch (options.LockProvider)
            {
                case OutboxLockProviderKind.Distributed:
                    {
                        // feature #82 应用层分布式锁：复用现有 IMultiLevelCache（L1+L2）+ IOptions<OutboxRelayOptions>
                        // service 注册（AddTenE0Caching 已注册，无需在本扩展新增 service）。
                        try
                        {
                            var cache = sp.GetRequiredService<IMultiLevelCache>();
                            return new DistributedOutboxLock(cache, sp.GetRequiredService<IOptions<OutboxRelayOptions>>());
                        }
                        catch
                        {
                            // IMultiLevelCache 未注册 / 解析失败 → 保守回退 NoOp，绝不抛异常
                            return new NoOpOutboxLock();
                        }
                    }

                case OutboxLockProviderKind.Leader:
                    {
                        // feature #82 Leader Election：复用 IMultiLevelCache + IAtomicCounter（AddTenE0Caching 注册）。
                        try
                        {
                            var cache = sp.GetRequiredService<IMultiLevelCache>();
                            var counter = sp.GetRequiredService<IAtomicCounter>();
                            return new LeaderElector(
                                cache,
                                counter,
                                sp.GetRequiredService<IOptions<OutboxRelayOptions>>());
                        }
                        catch
                        {
                            return new NoOpOutboxLock();
                        }
                    }

                case OutboxLockProviderKind.RowLock:
                    {
                        // RowLock 路径：创建临时 DbContext 探测 ProviderName（不持有状态，用完即释放）。
                        // 探测失败 / 未知 provider → 保守回退 NoOp，绝不抛异常破坏 Relay 启动。
                        IDbContextFactory<TContext> factory;
                        try
                        {
                            factory = sp.GetRequiredService<IDbContextFactory<TContext>>();
                        }
                        catch
                        {
                            return new NoOpOutboxLock();
                        }

                        string providerName;
                        try
                        {
                            using var probe = factory.CreateDbContext();
                            providerName = probe.Database.ProviderName ?? string.Empty;
                        }
                        catch
                        {
                            return new NoOpOutboxLock();
                        }

                        // switch 表达式（与 OutboxSchemaSeeder.BuildCreateIndexSql 同款命名匹配风格）。
                        // 注意：Npgsql.EntityFrameworkCore.PostgreSQL 是 PostgreSQL provider 的唯一命名空间，
                        // 删掉旧版 "Postgres" 兜底匹配 — 避免误中其他含 "Postgres" 子串的未知 provider。
                        return providerName switch
                        {
                            var p when p.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) =>
                                new SqlServerOutboxLock<TContext>(factory),
                            var p when p.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) =>
                                new PostgresOutboxLock<TContext>(factory),
                            // InMemory / 未知 provider：保守回退 NoOp，绝不抛异常。
                            _ => new NoOpOutboxLock(),
                        };
                    }

                case OutboxLockProviderKind.None:
                default:
                    {
                        // None / 默认 / 未知枚举值 → NoOpOutboxLock（0/1 实例部署零感知）
                        return new NoOpOutboxLock();
                    }
            }
        });

        return services;
    }
}
