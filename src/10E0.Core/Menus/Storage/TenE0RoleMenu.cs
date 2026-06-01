using TenE0.Core.Entities;

namespace TenE0.Core.Menus.Storage;

/// <summary>角色-菜单关联。多对多关系的中间表。</summary>
public sealed class TenE0RoleMenu : AuditedEntity
{
    public required string RoleCode { get; set; }
    public required string MenuId { get; set; }
}
