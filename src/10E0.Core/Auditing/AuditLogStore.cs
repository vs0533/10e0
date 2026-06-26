using Microsoft.EntityFrameworkCore;

namespace TenE0.Core.Auditing;

/// <summary>
/// <see cref="IAuditLogStore"/> 默认实现 —— 基于 <c>IDbContextFactory</c> 的只读查询服务。
///
/// <para>
/// 与写入路径（Channel + Worker）完全解耦：查询走独立 DbContext，不与后台写入争锁。
/// 分页参数在内部做边界规整（Page≥1、Size∈[1,200]），防止超大查询拖垮 DB。
/// </para>
/// </summary>
public sealed class AuditLogStore<TContext>(IDbContextFactory<TContext> contextFactory)
    : IAuditLogStore
    where TContext : DbContext
{
    private const int MaxSize = 200;

    public async Task<PagedResult<AuditLogDto>> QueryAsync(
        AuditLogQuery query, CancellationToken cancellationToken = default)
    {
        var (page, size) = NormalizePaging(query.Page, query.Size);

        await using var dc = await contextFactory.CreateDbContextAsync(cancellationToken);

        var q = dc.Set<TenE0AuditLog>().AsNoTracking();
        if (!string.IsNullOrEmpty(query.ActorCode))
            q = q.Where(a => a.ActorCode == query.ActorCode);
        if (!string.IsNullOrEmpty(query.EntityType))
            q = q.Where(a => a.EntityType == query.EntityType);
        if (!string.IsNullOrEmpty(query.EntityId))
            q = q.Where(a => a.EntityId == query.EntityId);
        if (!string.IsNullOrEmpty(query.Action))
            q = q.Where(a => a.Action == query.Action);
        if (query.From is not null)
            q = q.Where(a => a.CreateTime >= query.From);
        if (query.To is not null)
            q = q.Where(a => a.CreateTime <= query.To);

        var total = await q.LongCountAsync(cancellationToken);
        var items = await q
            .OrderByDescending(a => a.CreateTime)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(a => new AuditLogDto
            {
                Id = a.Id,
                CreateTime = a.CreateTime,
                TraceId = a.TraceId,
                ActorType = a.ActorType,
                ActorCode = a.ActorCode,
                EntityType = a.EntityType,
                EntityId = a.EntityId,
                Action = a.Action,
                ChangedFieldsJson = a.ChangedFieldsJson,
                IpAddress = a.IpAddress,
                UserAgent = a.UserAgent,
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<AuditLogDto>
        {
            Items = items,
            Total = total,
            Page = page,
            Size = size,
        };
    }

    public async Task<PagedResult<LoginLogDto>> QueryLoginsAsync(
        LoginLogQuery query, CancellationToken cancellationToken = default)
    {
        var (page, size) = NormalizePaging(query.Page, query.Size);

        await using var dc = await contextFactory.CreateDbContextAsync(cancellationToken);

        var q = dc.Set<TenE0LoginLog>().AsNoTracking();
        if (!string.IsNullOrEmpty(query.UserCode))
            q = q.Where(l => l.UserCode == query.UserCode);
        if (query.Success is not null)
            q = q.Where(l => l.Success == query.Success);
        if (query.From is not null)
            q = q.Where(l => l.CreateTime >= query.From);
        if (query.To is not null)
            q = q.Where(l => l.CreateTime <= query.To);

        var total = await q.LongCountAsync(cancellationToken);
        var items = await q
            .OrderByDescending(l => l.CreateTime)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(l => new LoginLogDto
            {
                Id = l.Id,
                CreateTime = l.CreateTime,
                UserCode = l.UserCode,
                EventType = l.EventType,
                Success = l.Success,
                IpAddress = l.IpAddress,
                UserAgent = l.UserAgent,
                FailureReason = l.FailureReason,
                ExpiresAt = l.ExpiresAt,
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<LoginLogDto>
        {
            Items = items,
            Total = total,
            Page = page,
            Size = size,
        };
    }

    private static (int page, int size) NormalizePaging(int page, int size)
    {
        page = page < 1 ? 1 : page;
        size = size < 1 ? 1 : (size > MaxSize ? MaxSize : size);
        return (page, size);
    }
}
