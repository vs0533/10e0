using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TenE0.Core.Observability.HealthChecks;

/// <summary>
/// DbContext 连通性健康检查（#161）。
///
/// <para>
/// 用 <c>AnyAsync()</c> 探测（而非 <c>SELECT 1</c>/<c>AddDbContextCheck</c>）：
/// <c>AnyAsync</c> 在关系型与 <b>InMemory</b> provider 上均成立 —— <c>SELECT 1</c>
/// 在 InMemory 下不适用，而 <c>AddDbContextCheck</c> 默认实现同样走 <c>SELECT 1</c>。
/// 探测任意一张小表即可验证"连接存活 + 能跑查询"。
/// </para>
/// </summary>
/// <typeparam name="TContext">应用 DbContext 类型。</typeparam>
public sealed class DbContextHealthCheck<TContext> : IHealthCheck
    where TContext : DbContext
{
    private readonly IDbContextFactory<TContext> _factory;

    /// <summary>构造。</summary>
    public DbContextHealthCheck(IDbContextFactory<TContext> factory)
    {
        _factory = factory;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // 用工厂而非注入 DbContext：与 OutboxRelayService / 后台任务一致的工厂模式，
            // 避免在 Singleton 探针里持有 Scoped DbContext 造成 captive dependency。
            await using var dc = await _factory.CreateDbContextAsync(cancellationToken);
            // 探测 OutboxMessage 表（框架内置、必有）。CountAsync 足以验证连接 + 元数据可用，
            // 比 AnyAsync 在多数 provider 上不更贵（都走 EXISTS/聚合）。
            await dc.Set<Events.Outbox.OutboxMessage>().AnyAsync(cancellationToken);
            return HealthCheckResult.Healthy("DbContext 连通正常");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("DbContext 探测失败", ex);
        }
    }
}
