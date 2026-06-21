using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace TenE0.Core.Events.Outbox;

/// <summary>
/// Outbox 毒消息管理服务 — 实现 <see cref="IOutboxAdmin"/> 契约。
///
/// <para>
/// 设计要点：
/// - 注入 <see cref="IServiceProvider"/> 而非直接注入 <see cref="IDbContextFactory{TContext}"/>：
///   与运维层 Admin 端点（如 Management API / 控制台脚本）的注册方式对齐 —
///   它们通常只把 <c>IDbContextFactory&lt;TContext&gt;</c> 注册到根容器，
///   由 Admin 类自行解析，避免重复声明 DbContext 泛型约束。
/// - 阈值 <c>MaxAttempts</c> 必须复用 <see cref="OutboxRelayOptions"/>，避免与 Relay 配置漂移
/// - 与 Relay 的查询条件对偶：
///   Relay 取 <c>SentTime IS NULL AND AttemptCount &lt; MaxAttempts</c>；
///   本类取 <c>SentTime IS NULL AND AttemptCount &gt;= MaxAttempts</c>
/// </para>
///
/// <para>
/// 注意 EF Core change tracking 语义：
/// <see cref="RetryPoisonMessageAsync"/> 通过查询加载实体（被追踪），修改属性后必须显式
/// <c>SaveChangesAsync</c> 才能落库；不要用 <c>AsNoTracking</c>，否则变更不会持久化。
/// </para>
/// </summary>
/// <typeparam name="TContext">承载 OutboxMessage 表的 EF Core DbContext 类型。</typeparam>
public sealed class OutboxAdminService<TContext> : IOutboxAdmin
    where TContext : DbContext
{
    private readonly IServiceProvider _serviceProvider;
    private readonly OutboxRelayOptions _options;

    public OutboxAdminService(IServiceProvider serviceProvider, IOptions<OutboxRelayOptions> options)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutboxMessage>> GetPoisonMessagesAsync(
        CancellationToken cancellationToken)
    {
        await using var ctx = await CreateContextAsync(cancellationToken);

        return await ctx.Set<OutboxMessage>()
            .Where(m => m.SentTime == null && m.AttemptCount >= _options.MaxAttempts)
            .OrderBy(m => m.OccurredOn)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> RetryPoisonMessageAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var idString = id.ToString("N");
        await using var ctx = await CreateContextAsync(cancellationToken);

        var message = await ctx.Set<OutboxMessage>()
            .FirstOrDefaultAsync(m => m.Id == idString, cancellationToken);

        if (message is null)
        {
            return false;
        }

        message.AttemptCount = 0;
        message.LastError = null;
        await ctx.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutboxPoisonMessageDto>> ExportPoisonMessagesAsync(
        CancellationToken cancellationToken)
    {
        // 复用 GetPoisonMessagesAsync 的查询结果做投影，避免重复遍历数据库
        var messages = await GetPoisonMessagesAsync(cancellationToken);
        return messages
            .Select(m => new OutboxPoisonMessageDto(
                Guid.Parse(m.Id),
                m.EventType,
                m.Payload,
                m.OccurredOn,
                m.AttemptCount,
                m.LastError))
            .ToList();
    }

    private Task<TContext> CreateContextAsync(CancellationToken cancellationToken)
    {
        var factory = _serviceProvider.GetRequiredService<IDbContextFactory<TContext>>();
        return factory.CreateDbContextAsync(cancellationToken);
    }
}
