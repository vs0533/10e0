using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using TenE0.Core.Abstractions;

namespace TenE0.Core.Auth;

/// <summary>
/// HTTP 场景下的 <see cref="ITenantContext"/> 实现（#11 multi-tenancy）。
///
/// 与 <see cref="HttpCurrentUserContext"/> 同模式：
/// - 同步属性只读 <see cref="ClaimsPrincipal"/>，零 I/O
/// - 不持有任何状态字段，无副作用 getter
/// - 委托给 <see cref="IHttpContextAccessor"/>（与 <see cref="HttpCurrentUserContext"/> 共享同一实例）
///
/// 取值规则：
/// - 读 <c>tenant_id</c> claim（<see cref="JwtClaims.TenantId"/>）
/// - null / 空白字符串 → 返回 null（让 EF Tenant Filter 走"安全默认"分支）
/// - HttpContext 为 null（未认证）→ 返回 null
///
/// 业务方升级指南：
/// - 业务 User 实体需继承 <c>TenE0User</c> 并加 <c>TenantId</c> 字段
/// - 登录成功后 <see cref="Jwt.Commands.LoginCommandHandler"/> 把 TenantId 写入 JWT
/// - 不需要任何额外接线：DI 在 <c>AddTenE0Core()</c> 自动注册本类
/// </summary>
internal sealed class HttpTenantContext(IHttpContextAccessor httpContextAccessor) : ITenantContext
{
    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    public string? TenantId
    {
        get
        {
            var raw = Principal?.FindFirstValue(JwtClaims.TenantId);
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }
    }
}
