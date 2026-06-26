namespace TenE0.Core.Auditing;

/// <summary>
/// 审计日志查询服务（管理后台用）。
///
/// <para>实现 <see cref="AuditLogStore{TContext}"/> 基于 <c>IDbContextFactory</c> 走只读查询，
/// 与写入路径（Channel + Worker）完全解耦 —— 查询永远不会阻塞写入，反之亦然。</para>
/// </summary>
public interface IAuditLogStore
{
    /// <summary>分页查询操作审计日志（支持按操作人 / 实体 / 动作 / 时间区间过滤）。</summary>
    Task<PagedResult<AuditLogDto>> QueryAsync(AuditLogQuery query, CancellationToken cancellationToken = default);

    /// <summary>分页查询登录审计日志（支持按用户 / 成败 / 时间区间过滤）。</summary>
    Task<PagedResult<LoginLogDto>> QueryLoginsAsync(LoginLogQuery query, CancellationToken cancellationToken = default);
}

// ================================================================
// Query / Result DTOs
// ================================================================

/// <summary>操作审计日志查询条件。所有字段可选，未传 = 不过滤。</summary>
public sealed class AuditLogQuery
{
    public string? ActorCode { get; set; }
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? Action { get; set; }
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }

    /// <summary>页码（从 1 开始）。小于 1 视为 1。</summary>
    public int Page { get; set; } = 1;
    /// <summary>每页大小。小于 1 视为 1，上限 200（防止超大查询拖垮 DB）。</summary>
    public int Size { get; set; } = 20;
}

/// <summary>登录审计日志查询条件。</summary>
public sealed class LoginLogQuery
{
    public string? UserCode { get; set; }
    /// <summary>null = 不过滤成败；true = 只看成败中成功；false = 只看失败。</summary>
    public bool? Success { get; set; }
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }
    public int Page { get; set; } = 1;
    public int Size { get; set; } = 20;
}

/// <summary>操作审计日志输出 DTO。</summary>
public sealed record AuditLogDto
{
    public string Id { get; init; } = "";
    public DateTimeOffset? CreateTime { get; init; }
    public string? TraceId { get; init; }
    public string ActorType { get; init; } = "";
    public string? ActorCode { get; init; }
    public string EntityType { get; init; } = "";
    public string EntityId { get; init; } = "";
    public string Action { get; init; } = "";
    public string ChangedFieldsJson { get; init; } = "[]";
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
}

/// <summary>登录审计日志输出 DTO。</summary>
public sealed record LoginLogDto
{
    public string Id { get; init; } = "";
    public DateTimeOffset? CreateTime { get; init; }
    public string UserCode { get; init; } = "";
    public string EventType { get; init; } = "";
    public bool Success { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public string? FailureReason { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>通用分页结果。</summary>
public sealed record PagedResult<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public long Total { get; init; }
    public int Page { get; init; }
    public int Size { get; init; }
}
