using TenE0.Core.Abstractions;
using TenE0.Core.Entities;

namespace TenE0.Core.Auth.Jwt.Storage;

/// <summary>
/// 框架内置的用户实体。
///
/// 业务系统可以：
/// - 直接用这个 — 简单场景够用
/// - 扩展继承（添加业务字段如手机/邮箱/部门）
/// - 完全替换 — 实现自己的 IUserStore，把 TenE0User 当作"参考实现"
///
/// 字段说明：
/// - <see cref="UserCode"/>：登录账号，全局唯一，作为权限和事件中的用户标识
/// - <see cref="PasswordHash"/>：PBKDF2 摘要（不是明文！）
/// - <see cref="IsActive"/>：禁用账号不影响其他数据，登录被拒
/// - <see cref="UserType"/>：个人/单位（沿用旧 E0 的双类型，新增字段可在子类）
/// </summary>
public class TenE0User : AuditedEntity
{
    public required string UserCode { get; set; }
    public required string DisplayName { get; set; }
    public required string PasswordHash { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public bool IsActive { get; set; } = true;
    public UserType UserType { get; set; } = UserType.Person;

    /// <summary>
    /// 租户 ID（#11 multi-tenancy）。登录时由 LoginCommandHandler 读取并写入 JWT
    /// "tenant_id" claim；后续请求由 HttpTenantContext 解析喂给 EF Tenant Filter。
    /// 业务方可继承扩展把 TenantId 设为 required（强制每个用户都归属租户）；
    /// 系统账号 / 多租户关闭场景可保持 nullable。
    /// </summary>
    public string? TenantId { get; set; }
}
