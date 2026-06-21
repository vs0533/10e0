using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace TenE0.Core.Events.Outbox;

/// <summary>
/// Outbox 行级锁 provider 静态选择器 + Resolver 抽象。
///
/// <para>
/// 文件结构：
/// <list type="bullet">
/// <item><see cref="OutboxLockProvider.ResolveLockType"/> — 按 <c>ProviderName</c> 字符串命名匹配，返回目标 <see cref="IOutboxLock"/> 实现的开放泛型 <see cref="Type"/>。</item>
/// <item><see cref="IOutboxRowLockResolver{TContext}"/> — 行级锁 provider 解析器契约：接受 <c>ProviderName</c> 字符串 + <see cref="OutboxRelayOptions.LockProvider"/>，返回具体 <see cref="IOutboxLock"/> 实例。</item>
/// <item><see cref="OutboxRowLockResolver{TContext}"/> — Resolver 默认实现（<c>internal sealed</c>，通过 <see cref="OutboxLockProviderServiceCollectionExtensions.AddOutboxRowLock{TContext}"/> 暴露）。</item>
/// <item><see cref="OutboxLockProviderServiceCollectionExtensions.AddOutboxRowLock{TContext}"/> — Resolver 的 DI 扩展（opt-in 路径）。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>匹配规则</b>（大小写不敏感）：
/// <list type="bullet">
/// <item>包含 <c>SqlServer</c> → <see cref="SqlServerOutboxLock{T}"/></item>
/// <item>包含 <c>Npgsql</c> → <see cref="PostgresOutboxLock{T}"/></item>
/// <item>其他（含 <c>InMemory</c> / 未知 / null / 空串）→ <see cref="NoOpOutboxLock"/></item>
/// </list>
/// </para>
///
/// <para>
/// <b>为什么字符串匹配而非绑具体 provider 包类型？</b>
/// 若让 <c>TenE0.Core</c> 直接 <c>using Microsoft.EntityFrameworkCore.SqlServer</c> /
/// <c>using Npgsql.EntityFrameworkCore.PostgreSQL</c>，会强加两个 NuGet 包到框架核心，
/// 而 0/1 实例部署根本用不到。按字符串命名匹配保持 TenE0.Core 与具体 provider 包解耦，
/// 选型权下放到调用方（10E0.Api 或业务项目自行引用 provider 包）。
/// </para>
/// </summary>
public static class OutboxLockProvider
{
    /// <summary>
    /// 按 <c>ProviderName</c> 字符串命名匹配，返回目标 lock 实现类型（泛型开放类型）。
    ///
    /// <para>
    /// 返回 <see cref="Type"/> 而非实例 — 选型在 DI 解析期完成一次（避免热路径反射），
    /// 实例化由 DI 容器 / 解析器负责（用具体 DbContext 类型闭合后注入
    /// 对应 <see cref="IDbContextFactory{TContext}"/>）。
    /// </para>
    /// </summary>
    /// <param name="providerName">EF Core <c>Database.ProviderName</c> 字符串。null / 空串 / 未知都安全回退 <see cref="NoOpOutboxLock"/>。</param>
    /// <returns>具体 <see cref="IOutboxLock"/> 实现的 <see cref="Type"/>（泛型开放类型，调用方需用 <c>MakeGenericType</c> 闭合）。</returns>
    public static Type ResolveLockType(string? providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return typeof(NoOpOutboxLock);
        }

        if (providerName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            // 泛型开放类型：具体 TContext 由调用方闭合
            return typeof(SqlServerOutboxLock<>);
        }

        if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            return typeof(PostgresOutboxLock<>);
        }

        // 含 InMemory / 未知 provider：保守回退 NoOp，绝不抛异常
        return typeof(NoOpOutboxLock);
    }
}

/// <summary>
/// 行级锁 provider 解析器契约 — 接受 <c>ProviderName</c> 字符串并返回对应 <see cref="IOutboxLock"/> 实例。
///
/// <para>
/// 设计动机：<see cref="OutboxLockProviderKind"/> 配置 + 底层 <c>ProviderName</c> 字符串
/// 共同决定最终注入哪个 <see cref="IOutboxLock"/> 实现。把这个决策点抽成独立接口
/// （而非塞到 <see cref="OutboxLockProviderKind"/> 枚举上）的好处：
/// - 单元可测：测试时直接 <c>resolver.Resolve("Microsoft.EntityFrameworkCore.SqlServer")</c>，
///   不必构造真实 DbContext 实例去探测 <c>ProviderName</c>。
/// - DI 解耦：Relay 编排代码只拿 <see cref="IOutboxLock"/>，不关心选型过程。
/// </para>
///
/// <para>
/// <b>opt-in 使用</b>：本契约不在 <c>AddTenE0DomainEvents</c> 默认路径里 — 业务方如需
/// "RowLock + 自定义 provider 实现"（例如同时支持 Npgsql + 某种私有 provider），
/// 可显式 <c>services.AddOutboxRowLock&lt;MyDbContext&gt;()</c> 后再 <c>Replace</c>
/// <see cref="IOutboxRowLockResolver{TContext}"/> 为自己的实现。默认 RowLock 路径走
/// <see cref="OutboxLockingServiceCollectionExtensions.AddOutboxLocking{TContext}"/> 的内联 switch。
/// </para>
/// </summary>
/// <typeparam name="TContext">承载 OutboxMessage 表的 EF Core DbContext 类型。</typeparam>
public interface IOutboxRowLockResolver<TContext>
    where TContext : DbContext
{
    /// <summary>
    /// 按 <paramref name="providerName"/> 字符串 + 配置中的 <see cref="OutboxLockProviderKind"/>
    /// 共同决策，返回具体 <see cref="IOutboxLock"/> 实例。
    /// </summary>
    /// <param name="providerName">EF Core <c>Database.ProviderName</c> 字符串（由调用方在解析期从 DbContext 上读取）。</param>
    /// <returns>具体 <see cref="IOutboxLock"/> 实现；任何异常路径都回退 <see cref="NoOpOutboxLock"/> 而非抛错。</returns>
    IOutboxLock Resolve(string providerName);

    /// <summary>
    /// 在 DI 委托工厂场景下使用 — 内部创建临时 DbContext 探测 <c>ProviderName</c>，
    /// 然后走 <see cref="Resolve(string)"/> 决策。
    /// </summary>
    /// <returns>具体 <see cref="IOutboxLock"/> 实现；探测失败时回退 <see cref="NoOpOutboxLock"/>。</returns>
    IOutboxLock ResolveWithProbe();
}

/// <summary>
/// <see cref="IOutboxRowLockResolver{TContext}"/> 的默认实现 — 读 <see cref="OutboxRelayOptions"/> +
/// <see cref="OutboxLockProvider.ResolveLockType"/> 共同决策。
/// </summary>
/// <typeparam name="TContext">承载 OutboxMessage 表的 EF Core DbContext 类型。</typeparam>
internal sealed class OutboxRowLockResolver<TContext> : IOutboxRowLockResolver<TContext>
    where TContext : DbContext
{
    private readonly IDbContextFactory<TContext> _factory;
    private readonly OutboxRelayOptions _options;

    public OutboxRowLockResolver(
        IDbContextFactory<TContext> factory,
        IOptions<OutboxRelayOptions> options)
    {
        _factory = factory;
        _options = options.Value;
    }

    /// <inheritdoc />
    public IOutboxLock Resolve(string providerName)
    {
        // LockProvider 选项优先级：
        // 1. None → 无论 ProviderName 一律 NoOp（0/1 实例部署）
        // 2. Distributed → 本任务未实现，回退 NoOp（不抛异常）
        // 3. RowLock → 走 ProviderName 命名匹配（InMemory / 未知仍回退 NoOp）
        if (_options.LockProvider != OutboxLockProviderKind.RowLock)
        {
            return new NoOpOutboxLock();
        }

        var lockType = OutboxLockProvider.ResolveLockType(providerName);
        if (lockType == typeof(NoOpOutboxLock))
        {
            return new NoOpOutboxLock();
        }

        // 泛型开放类型（SqlServerOutboxLock<> / PostgresOutboxLock<>）用 TContext 闭合后实例化
        var closedType = lockType.MakeGenericType(typeof(TContext));
        return (IOutboxLock)Activator.CreateInstance(closedType, _factory)!;
    }

    /// <inheritdoc />
    public IOutboxLock ResolveWithProbe()
    {
        // 同步创建 DbContext 探测 ProviderName（不持有 DbContext 状态，用完即释放）
        try
        {
            using var probe = _factory.CreateDbContext();
            return Resolve(probe.Database.ProviderName ?? string.Empty);
        }
        catch
        {
            // 探测失败：保守回退 NoOp，绝不抛异常破坏 Relay 启动
            return new NoOpOutboxLock();
        }
    }
}

/// <summary>
/// Outbox 行级锁 Resolver 的 DI 注册扩展（opt-in 路径）。
/// </summary>
public static class OutboxLockProviderServiceCollectionExtensions
{
    /// <summary>
    /// 注册 <see cref="IOutboxRowLockResolver{TContext}"/> 默认实现 + 委托工厂形式的 <see cref="IOutboxLock"/>。
    ///
    /// <para>
    /// 解析时由 <see cref="IOutboxRowLockResolver{TContext}"/> 实际决策（创建临时 DbContext 探测
    /// <c>ProviderName</c>，再走字符串命名匹配）。
    /// </para>
    ///
    /// <para>
    /// <b>opt-in 路径</b>：默认 <c>AddTenE0DomainEvents</c> 走的是
    /// <see cref="OutboxLockingServiceCollectionExtensions.AddOutboxLocking{TContext}"/> 的内联 switch；
    /// 业务方如需覆盖 Resolver 行为，可显式调本方法后再 <c>Replace</c>。
    /// </para>
    /// </summary>
    /// <typeparam name="TContext">承载 OutboxMessage 表的 EF Core DbContext 类型。</typeparam>
    /// <param name="services">DI 容器。</param>
    /// <returns>同一 <see cref="IServiceCollection"/> 以便链式调用。</returns>
    public static IServiceCollection AddOutboxRowLock<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.TryAddSingleton<IOutboxRowLockResolver<TContext>, OutboxRowLockResolver<TContext>>();

        // IOutboxLock 注册为委托：解析时由 Resolver 创建临时 DbContext 探测 ProviderName 后选型
        services.TryAddSingleton<IOutboxLock>(sp =>
        {
            var resolver = sp.GetRequiredService<IOutboxRowLockResolver<TContext>>();
            // 委托工厂场景：无法预知 providerName，由 Resolver 内部探测
            return resolver.ResolveWithProbe();
        });

        return services;
    }
}
