using TenE0.Core.Entities;

namespace TenE0.Core.Permissions.Storage;

/// <summary>
/// 角色实体。Code 作为业务主键（同时也是 PK），与 JWT role claim 直接匹配。
///
/// 设计取舍：
/// - 不用 GUID Id 作 PK，因为 Code 已是稳定的全局标识，省一次 join
/// - 字符串 PK 长度 64 足够（"viewer" / "org_admin" / "tenant_42_owner" 等）
/// - 实体仍继承 AuditedEntity（含 CreateTime/UpdateBy/IsSoftDelete）— 但 Id 字段废弃不用
/// </summary>
public class TenE0Role : AuditedEntity
{
    /// <summary>角色业务编码，与 JWT role claim 一致。</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }
    public string? Description { get; set; }
}

/// <summary>
/// 角色 → 权限 key 的 grant 记录。系统管理表，不开放给业务扩展。
/// </summary>
public sealed class TenE0RolePermission : AuditedEntity
{
    public required string RoleCode { get; set; }
    public required string PermissionKey { get; set; }
}
