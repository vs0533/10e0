using TenE0.Core.Entities;

namespace TenE0.Core.Auth.Jwt.Storage;

/// <summary>用户-角色关联。多对多关系的中间表。</summary>
public sealed class TenE0UserRole : AuditedEntity
{
    public required string UserCode { get; set; }
    public required string RoleCode { get; set; }
}
