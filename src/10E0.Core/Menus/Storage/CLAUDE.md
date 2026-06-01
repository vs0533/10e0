# Menus/Storage/ — 菜单实体与 EF 映射

## 文件说明

| 文件 | 职责 |
|------|------|
| `TenE0Menu.cs` | 菜单实体：Id、Code、Name、Path（前端路由）、Component（前端组件）、Icon、ParentId、Order、MenuType。继承 `AuditedEntity` |
| `TenE0RoleMenu.cs` | 角色-菜单多对多关联表 |
| `MenuModelBuilderExtensions.cs` | EF Core 表映射：Menu 的自关联索引、RoleMenu 的联合主键 |
