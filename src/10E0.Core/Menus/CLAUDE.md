# Menus/ — 菜单树管理

菜单树的 CRUD、查询和角色分配。

## 文件说明

| 文件 | 职责 |
|------|------|
| `IMenuService.cs` | 菜单服务接口 |
| `MenuService.cs` | 菜单树 CRUD：Add、Update、Delete、Move |
| `MenuService.Queries.cs` | 查询方法：GetTree（全量）、GetUserTree（基于用户角色的菜单） |
| `MenuService.RoleAssign.cs` | 角色-菜单分配：AssignMenusToRole、GetRoleMenus |
| `MenuType.cs` | 菜单类型枚举：`Directory=0`（目录）、`Menu=1`（页面）、`Button=2`（按钮） |
| `Dtos.cs` | 请求/响应 DTO |

## 子目录

| 目录 | 职责 |
|------|------|
| `Storage/` | 菜单实体 + EF 映射 |

## 菜单树结构

菜单使用 `ParentId` 构建父子关系，`Order` 字段控制排序。支持三级嵌套（目录 → 页面 → 按钮）。

## 对比旧版

- 旧版 `E0Menu : TreeEntity<E0Menu>` 用泛型自引用
- 新版 `TenE0Menu` 非泛型，继承 `AuditedEntity`，手动管理 `ParentId`
