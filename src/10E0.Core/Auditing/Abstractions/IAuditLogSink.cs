namespace TenE0.Core.Auditing;

/// <summary>
/// 审计日志写入抽象。
///
/// <para>
/// 设计为<b>非阻塞</b>：默认实现 <see cref="AuditLogSink"/> 把条目推入内存 Channel，
/// 由后台 <c>AuditLogRelayWorker</c> 批量落库（best-effort，失败不阻断业务）。
/// 这样业务 <c>SaveChanges</c> 路径不会因审计写入而变慢，审计失败也永不回滚业务事务。
/// </para>
/// </summary>
public interface IAuditLogSink
{
    /// <summary>
    /// 入队一条操作审计日志（字段级 diff）。
    /// 非阻塞：写入 Channel 即返回，由后台 worker 异步落库。
    /// </summary>
    /// <param name="entry">待落库的审计条目（调用方填好业务字段；CreateTime 由 Sink 补）。</param>
    /// <param name="cancellationToken"></param>
    Task EnqueueAsync(AuditLogEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// 入队一条登录审计日志（登录/登出/刷新/失败）。
    /// 同样非阻塞走 Channel。
    /// </summary>
    Task WriteLoginAsync(LoginLogEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>
/// 操作审计日志的内存载体（落库前形态）。由 <see cref="AuditLogInterceptor"/> 构造。
/// 与持久化实体 <see cref="TenE0AuditLog"/> 字段一一对应，但用 record + required 表达"构造即完整"。
/// </summary>
public sealed record AuditLogEntry
{
    public string? TraceId { get; init; }
    public string ActorType { get; init; } = "User";
    public string? ActorCode { get; init; }
    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    /// <summary>Create / Update / Delete / SoftDelete。</summary>
    public required string Action { get; init; }
    /// <summary>已序列化好的字段 diff JSON（已脱敏）。</summary>
    public required string ChangedFieldsJson { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    /// <summary>由 Sink 填充（统一时间源，便于测试用 FakeTimeProvider）。</summary>
    public DateTimeOffset CreateTime { get; set; }
}

/// <summary>
/// 登录审计日志的内存载体。由 auth command handlers 构造。
/// </summary>
public sealed record LoginLogEntry
{
    public required string UserCode { get; init; }
    /// <summary>Login / Logout / Refresh / Failed。</summary>
    public required string EventType { get; init; }
    public bool Success { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public string? FailureReason { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    /// <summary>由 Sink 填充（统一时间源）。</summary>
    public DateTimeOffset CreateTime { get; set; }
}
