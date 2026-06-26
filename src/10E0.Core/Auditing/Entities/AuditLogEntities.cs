using TenE0.Core.Abstractions;
using TenE0.Core.Entities;

namespace TenE0.Core.Auditing;

/// <summary>
/// 操作审计日志 — 记录"谁在何时对哪条业务数据做了什么 CUD，改了哪些字段"。
///
/// <para>
/// 继承 <see cref="ITimerEntity"/> 而非 <see cref="AuditedEntity"/>：
/// 审计日志是<b>只追加</b>的历史轨迹，永不软删除、永不被软删除 Query Filter 隐藏。
/// <see cref="CreateTime"/> 由现有 <c>AuditInterceptor</c> 自动填充（审计自身的审计元数据）。
/// </para>
/// </summary>
public sealed class TenE0AuditLog : TimedEntity
{
    /// <summary>
    /// 请求链路追踪 ID（来自 <c>Activity.Current.TraceId</c>）。
    /// 无分布式追踪上下文时为 null；用于把审计事件与 APM/日志关联。
    /// </summary>
    public string? TraceId { get; set; }

    /// <summary>操作来源类型：User（HTTP 请求）/ System（后台 Job）/ ApiKey 等。默认 "User"。</summary>
    public string ActorType { get; set; } = "User";

    /// <summary>操作人编码（用户 Code）。无 HTTP 上下文（Seeder/后台 Worker）时为 null。</summary>
    public string? ActorCode { get; set; }

    /// <summary>被审计实体的 CLR 类型名（简化显示用，不含命名空间）。</summary>
    public string EntityType { get; set; } = "";

    /// <summary>被审计实体的主键。</summary>
    public string EntityId { get; set; } = "";

    /// <summary>动作种类：Create / Update / Delete / SoftDelete。</summary>
    public string Action { get; set; } = "";

    /// <summary>
    /// 字段级 diff JSON：<c>[{"f":"Name","o":"old","n":"new"}]</c>。
    /// 仅记录标量字段变更（导航属性 / 集合默认跳过，避免 JSON 爆炸，见 issue #152 决策点 #3）。
    /// 敏感字段（Password/Token/Secret/SigningKey/ApiKey）经 <c>IAuditFieldFilter</c> 脱敏为 "***"。
    /// Create 动作下 o=null；Delete 动作下 n=null。
    /// </summary>
    public string ChangedFieldsJson { get; set; } = "[]";

    /// <summary>客户端 IP（来自 HttpContext.Connection.RemoteIpAddress）。无 HTTP 上下文时为 null。</summary>
    public string? IpAddress { get; set; }

    /// <summary>客户端 User-Agent。无 HTTP 上下文时为 null。</summary>
    public string? UserAgent { get; set; }
}

/// <summary>
/// 登录审计日志 — 记录登录/登出/刷新 token 的时间、来源、成败与失败原因。
///
/// <para>由 <c>LoginCommandHandler</c> / <c>RefreshTokenCommandHandler</c> /
/// <c>LogoutCommandHandler</c> 在成功与失败两条路径上通过 <c>IAuditLogSink.WriteLoginAsync</c> 埋点。
/// 同 <see cref="TenE0AuditLog"/>，只追加不删除。</para>
/// </summary>
public sealed class TenE0LoginLog : TimedEntity
{
    /// <summary>用户编码。失败登录场景下可能是攻击者尝试的用户名（仍如实记录）。</summary>
    public string UserCode { get; set; } = "";

    /// <summary>事件种类：Login / Logout / Refresh / Failed。</summary>
    public string EventType { get; set; } = "";

    /// <summary>本次认证是否成功。</summary>
    public bool Success { get; set; }

    /// <summary>客户端 IP。无 HTTP 上下文时为 null。</summary>
    public string? IpAddress { get; set; }

    /// <summary>客户端 User-Agent。无 HTTP 上下文时为 null。</summary>
    public string? UserAgent { get; set; }

    /// <summary>失败原因（成功时为 null）。如 "用户名或密码错误" / "账号已被禁用"。</summary>
    public string? FailureReason { get; set; }

    /// <summary>本次签发的 refresh token 过期时间（仅登录/刷新成功时填）。</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}
