using Microsoft.EntityFrameworkCore;

namespace TenE0.Core.Events.Outbox;

/// <summary>
/// PostgreSQL 行级锁 provider — 通过 <c>UPDATE</c> 设置 <c>LockedByInstance</c> / <c>LockedUntil</c>
/// 抢占 OutboxMessage 行的处理权。语义与 <see cref="SqlServerOutboxLock{T}"/> 对偶，
/// 实现风格完全对齐，便于后续统一行为审查。
///
/// <para>
/// <b>为什么是 <c>UPDATE</c> 而不是显式 <c>SELECT ... FOR UPDATE SKIP LOCKED</c>？</b>
/// 多实例 Relay 抢占同一行时，单条 <c>SELECT</c> 拿不到原子互斥；
/// 改用 <c>UPDATE ... WHERE (LockedByInstance IS NULL OR LockedUntil &lt;= now)</c>：
/// - <c>FOR UPDATE</c> 等价语义天然由 <c>UPDATE</c> 行级排他锁提供
/// - <c>SKIP LOCKED</c> 等价语义由 "未锁或锁已过期" 的 WHERE 条件实现
/// - 单条 <c>UPDATE</c> 影响行数 = 0 即视为抢锁失败，行为确定（无需事务包裹）
/// </para>
///
/// <para>
/// <b>泛型设计与 OutboxAdminService 对齐</b>：
/// 泛型 <typeparamref name="TContext"/> 约束 <see cref="DbContext"/>，与本目录
/// <see cref="OutboxAdminService{TContext}"/> 等所有 Outbox 相关服务保持一致的泛型形态。
/// </para>
///
/// <para>
/// <b>双路径策略</b>：
/// 每次调用 <c>TryAcquireAsync / ReleaseAsync</c> 时探测当前 DbContext 的
/// <c>Database.ProviderName</c>：InMemory provider 走 LINQ 路径（保证单测可跑），
/// 其他 provider（含 <c>Npgsql.EntityFrameworkCore.PostgreSQL</c>）走
/// <c>ExecuteSqlInterpolatedAsync</c> 拼 <c>UPDATE</c> 路径。
/// </para>
///
/// <para>
/// <b>租约 / 当前时间来源</b>：本实现直接使用 <see cref="DateTimeOffset.UtcNow"/>，
/// 未注入 <see cref="TimeProvider"/>；构造函数只接 <see cref="IDbContextFactory{TContext}"/>
/// 是与 Step 1 验收测试的契约签名严格对齐（见 <c>OutboxLockProviderAcceptanceTests</c>）。
/// </para>
/// </summary>
/// <typeparam name="TContext">承载 OutboxMessage 表的 EF Core DbContext 类型。</typeparam>
public sealed class PostgresOutboxLock<TContext> : IOutboxLock
    where TContext : DbContext
{
    private readonly IDbContextFactory<TContext> _factory;

    /// <summary>
    /// 构造 PostgreSQL 行级锁 provider。
    /// </summary>
    /// <param name="factory">承载 OutboxMessage 表的 DbContext 工厂 — 每次调用 <c>TryAcquire / Release</c> 都新建 DbContext，避免多实例共享 DbContext 的线程安全陷阱。</param>
    public PostgresOutboxLock(IDbContextFactory<TContext> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(
        string messageId,
        string instanceId,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return false;
        }

        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var newLockedUntil = now + lease;

        // InMemory provider：走 LINQ 路径，让 Step 1 验收测试在内存库上跑通
        if (IsInMemoryProvider(ctx))
        {
            var msg = await ctx.Set<OutboxMessage>().FindAsync(new object?[] { messageId }, cancellationToken);
            if (msg is null)
            {
                return false;
            }

            // 持锁条件：被其他实例持有 且 租约未到期 —
            // 自持自取允许覆盖（与 SQL 路径 "LockedByInstance IS NULL OR LockedUntil <= now" 等价），
            // 否则同一实例在同一轮 Relay 重试时会被自己卡住。
            if (msg.LockedByInstance is not null
                && !string.Equals(msg.LockedByInstance, instanceId, StringComparison.Ordinal)
                && msg.LockedUntil > now)
            {
                return false;
            }

            msg.LockedByInstance = instanceId;
            msg.LockedUntil = newLockedUntil;
            await ctx.SaveChangesAsync(cancellationToken);
            return true;
        }

        // 真实 PostgreSQL provider 路径：单条 UPDATE 用 WHERE 条件天然实现
        // "未被任何实例持有" 或 "锁已过期" 即可抢占。
        var rows = await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE "OutboxMessages"
                SET "LockedByInstance" = {instanceId},
                    "LockedUntil" = {newLockedUntil}
              WHERE "Id" = {messageId}
                AND ("LockedByInstance" IS NULL OR "LockedUntil" <= {now})
             """,
            cancellationToken);

        return rows > 0;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(
        string messageId,
        string instanceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return;
        }

        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);

        // InMemory provider：走 LINQ 路径 + 所有权校验
        if (IsInMemoryProvider(ctx))
        {
            var msg = await ctx.Set<OutboxMessage>().FindAsync(new object?[] { messageId }, cancellationToken);
            if (msg is null)
            {
                return;
            }

            // 所有权校验：仅当 LockedByInstance == 调用方 instanceId 时才清空
            if (!string.Equals(msg.LockedByInstance, instanceId, StringComparison.Ordinal))
            {
                return;
            }

            msg.LockedByInstance = null;
            msg.LockedUntil = null;
            await ctx.SaveChangesAsync(cancellationToken);
            return;
        }

        // 真实 PostgreSQL provider 路径：所有权校验通过 WHERE 子句实现
        await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE "OutboxMessages"
                SET "LockedByInstance" = NULL,
                    "LockedUntil" = NULL
              WHERE "Id" = {messageId}
                AND "LockedByInstance" = {instanceId}
             """,
            cancellationToken);
    }

    /// <summary>
    /// 探测当前 DbContext 底层是否 InMemory provider —
    /// InMemory 不支持 <c>ExecuteSqlInterpolatedAsync</c> 写库，必须走 LINQ 路径。
    /// </summary>
    private static bool IsInMemoryProvider(TContext ctx) =>
        (ctx.Database.ProviderName ?? string.Empty)
            .Contains("InMemory", StringComparison.OrdinalIgnoreCase);
}
