namespace TenE0.Core.Abstractions;

/// <summary>
/// 当前租户上下文（#11 multi-tenancy）。
///
/// 与 <see cref="ICurrentUserContext"/> 对偶：用户上下文回答"谁在操作"，
/// 租户上下文回答"操作属于哪个租户"。EF Tenant Named Query Filter 读取
/// <see cref="TenantId"/> 自动追加 <c>e.TenantId == tenantContext.TenantId</c> 条件，
/// 实现跨租户隔离。
///
/// 设计要点（与 ICurrentUserContext 一致）：
/// - 同步属性只读 ClaimsPrincipal，零 I/O
/// - 无副作用 getter，不抛异常
/// - 未认证 / 无 tenant_id claim → 返回 null（让 EF Filter 走"safe-by-default"分支隐藏所有行）
///
/// 实现：
/// - <c>HttpTenantContext</c>（HTTP 请求场景）— 从 JWT "tenant_id" claim 读取
/// - 后台 Worker / 测试可使用 AsyncLocal 实现（与 <see cref="Auth.AmbientCurrentUserContext"/> 同模式）
/// </summary>
public interface ITenantContext
{
    /// <summary>
    /// 当前租户 ID。未登录 / 无 tenant_id claim / 多租户关闭的系统账号 → 返回 null。
    /// EF Tenant Filter 把 <c>null</c> 解释为"无租户上下文，可见 0 行"的安全默认。
    /// </summary>
    string? TenantId { get; }
}
