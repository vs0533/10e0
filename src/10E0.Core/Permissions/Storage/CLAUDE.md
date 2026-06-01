# Permissions/Storage/ — 角色实体与权限存储

## 文件说明

| 文件 | 职责 |
|------|------|
| `TenE0Role.cs` | 角色实体：`Code`（全局唯一标识）、`Name`（显示名）。继承 `AuditedEntity`。注意：`Id` 字段废弃不用，`Code` 才是实际 PK |
| `EfPermissionStore.cs` | `IPermissionStore` 的 EF Core 实现：从 `RolePermission` 关联表查询角色的权限集合 |
| `PermissionModelBuilderExtensions.cs` | EF Core 表映射：Role、UserRole、RolePermission、RoleMenu 的配置 |

## 关于 TenE0Role.Id

`TenE0Role` 继承 `AuditedEntity`（有 GUID `Id`），但设计上 `Code` 才是全局标识。`Id` 字段虽然被创建和填充，但业务逻辑中不使用。EF 配置了 `Code` 的唯一索引。

## 关联表

| 表 | 关系 |
|------|------|
| `TenE0UserRole` | 用户 ↔ 角色（多对多） |
| `TenE0RolePermission` | 角色 ↔ 权限 key（多对多） |
| `TenE0RoleMenu` | 角色 ↔ 菜单（多对多，实体定义在 `Menus/Storage/`） |
