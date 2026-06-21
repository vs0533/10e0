using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
/// 多实例部署时把 <see cref="IOutboxLock"/> Replace 为具体 provider 实现即可，Relay 编排代码零改动。
/// </para>
/// </summary>
public static class OutboxLockingServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Outbox 行级锁默认实现 + 配置 <see cref="OutboxLockOptions"/>。
    /// 返回同一 <see cref="IServiceCollection"/> 以便链式调用。
    /// </summary>
    public static IServiceCollection AddOutboxLocking(this IServiceCollection services)
    {
        // 默认实现：NoOpOutboxLock。无外部依赖，可直接单例。
        services.TryAddSingleton<IOutboxLock, NoOpOutboxLock>();

        // 保留 OutboxLockOptions 注册以兼容老配置代码。运行时参数的真实权威来源仍是 OutboxRelayOptions。
        services.TryAddSingleton(new OutboxLockOptions());

        return services;
    }
}
