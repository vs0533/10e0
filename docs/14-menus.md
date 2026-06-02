# 菜单管理（Menus）

## 概览

菜单管理模块提供**无限层级菜单树**的完整生命周期管理，包括菜单实体的 CRUD、子树移动、角色分配、用户可见菜单过滤等功能。与组织架构（Orgs）共用相同的**物化路径（Materialized Path）**树模型。

**NuGet 注册方式**：`services.AddTenE0Menus<TContext>()`

---

## TenE0Menu 实体

继承链：`BaseEntity` → `TimedEntity` → `AuditedEntity` → `TreeAuditedEntity`

集成自动审计、软删除、时间戳等基础设施能力。

| 字段 | 类型 | 说明 |
|------|------|------|
| `Name` | `string` | 菜单显示名称 |
| `RoutePath` | `string` | 前端路由路径（如 `/system/users`，区别于物化路径 `TreePath`） |
| `Icon` | `string?` | 菜单图标（前端图标组件名或 URL） |
| `Component` | `string?` | 前端组件路径（如 `views/system/UserList.vue`） |
| `Redirect` | `string?` | 重定向路径（目录节点自动跳转到默认子路由） |
| `Layout` | `bool` | 是否使用布局组件包裹（默认 `true`，即带侧边栏/顶栏的完整页面布局） |
| `Order` | `int` | 同级排序权重（值越小越靠前） |
| `IsActive` | `bool` | 是否启用（`false` 时前端不渲染该菜单） |
| `MenuType` | `MenuType` | 菜单类型（Menu / Directory / Button / Link） |
| `TreePath` | `string` | 物化路径，格式 `/{id1}/{id2}/{id3}/`，首尾各一个 `/` |
| `Level` | `int` | 树层级深度（根 = 0） |
| `Children` | `List<TenE0Menu>?` | 子菜单集合（仅内存使用，`[NotMapped]`，不映射到数据库） |

### 物化路径约定

```text
根节点：TreePath = "/{rootId}/",      Level = 0
子节点：TreePath = "/{rootId}/{childId}/", Level = 1

子树查询：dc.TenE0Menus.Where(m => m.TreePath.StartsWith(node.TreePath))
```

> `TreePath` 和 `Level` 由 `MenuService` 统一维护，业务代码不应直接修改。

---

## MenuType 枚举

| 值 | 名称 | 说明 |
|----|------|------|
| `0` | `Directory` | 目录节点：用于分组，本身不产生路由导航 |
| `1` | `Menu` | 菜单项：对应具体页面，可导航 |
| `2` | `Button` | 按钮/权限点：不产生路由，仅用于页面内细粒度权限控制 |
| `3` | `Link` | 外链：点击后跳转至外部 URL |

三级粒度覆盖了前端菜单树的常见分层：**目录 → 页面 → 按钮**。

---

## IMenuService 接口

```csharp
public interface IMenuService
{
    Task<TenE0Menu> AddAsync(MenuCreateRequest request, CancellationToken ct);
    Task UpdateAsync(string menuId, MenuUpdateRequest request, CancellationToken ct);
    Task DeleteAsync(string menuId, CancellationToken ct);
    Task MoveAsync(string menuId, string? newParentId, CancellationToken ct);
    Task<IReadOnlyList<TenE0Menu>> GetAllAsync(CancellationToken ct);
    Task<IReadOnlyList<MenuTreeNode>> GetMenuTreeAsync(CancellationToken ct);
    Task<IReadOnlyList<TenE0Menu>> GetUserMenusAsync(CancellationToken ct);
    Task<IReadOnlyList<MenuTreeNode>> GetUserMenuTreeAsync(CancellationToken ct);
    Task AssignToRoleAsync(string roleCode, IEnumerable<string> menuIds, CancellationToken ct);
    Task<IReadOnlySet<string>> GetRoleMenuIdsAsync(string roleCode, CancellationToken ct);
}
```

### 方法说明

| 方法 | 功能 |
|------|------|
| `AddAsync` | 创建菜单节点，自动根据 `ParentId` 计算 `TreePath` 和 `Level`；无 ParentId 则视为根节点 |
| `UpdateAsync` | 部分更新：`MenuUpdateRequest` 中所有字段可空，`null` 表示不修改该字段 |
| `DeleteAsync` | **软删除**：设置 `IsSoftDelete = true`，记录删除时间和操作人；已删除菜单自动被全局查询过滤器屏蔽 |
| `MoveAsync` | 移动节点到新父节点（含子树级联更新 `TreePath` / `Level`）；自动校验：不能移到自身、不能移到自己的后代节点 |
| `GetAllAsync` | 获取全部未删除菜单的平铺列表（按 `Order` 排序） |
| `GetMenuTreeAsync` | 构建完整菜单树（`MenuTreeNode` 嵌套结构） |
| `GetUserMenusAsync` | 获取当前用户可见的菜单平铺列表：根据用户角色关联的 `TenE0RoleMenu` 过滤，且只返回 `IsActive = true` 的菜单 |
| `GetUserMenuTreeAsync` | 获取当前用户可见的菜单树：自动收集所有祖先节点确保树不断裂 |
| `AssignToRoleAsync` | **批量替换**角色的菜单分配：读取当前关联计算 diff，删除多余、新增缺少 |
| `GetRoleMenuIdsAsync` | 查询指定角色已分配的菜单 ID 集合 |

### 请求 DTO

```csharp
// 创建菜单 — 必填：Name、RoutePath、Order、MenuType
public record MenuCreateRequest(
    string Name, string RoutePath, string? ParentId,
    string? Icon, string? Component, string? Redirect,
    bool Layout, int Order, MenuType MenuType);

// 更新菜单 — 所有字段可空，null = 不修改
public record MenuUpdateRequest(
    string? Name, string? RoutePath,
    string? Icon, string? Component, string? Redirect,
    bool? Layout, int? Order, MenuType? MenuType, bool? IsActive);
```

### 角色-菜单关联（TenE0RoleMenu）

菜单与角色之间通过中间表 `TenE0RoleMenu` 实现 M:N 多对多关系：

```csharp
public sealed class TenE0RoleMenu : AuditedEntity
{
    public required string RoleCode { get; set; }
    public required string MenuId { get; set; }
}
```

分配策略是**全量替换 + diff**：`AssignToRoleAsync` 读取当前角色关联，与新列表求差集后只执行增删操作。这种方法避免了大事务全量删除再插入的问题。

---

## API 端点

| 方法 | 路由 | 说明 |
|------|------|------|
| `GET` | `/menus/tree` | 获取完整菜单树（公开，无需管理员权限） |
| `GET` | `/menus/user-tree` | 获取当前用户可见菜单树（按角色过滤 + 祖先补齐） |
| `POST` | `/admin/menus` | 创建菜单（Body: `MenuCreateRequest`） |
| `PUT` | `/admin/menus/{id}` | 部分更新菜单（Body: `MenuUpdateRequest`） |
| `DELETE` | `/admin/menus/{id}` | 软删除菜单及其子树 |
| `PUT` | `/admin/menus/{id}/move` | 移动菜单到新父节点（Query: `parentId`） |
| `PUT` | `/admin/roles/{code}/menus` | 批量替换角色的菜单分配（Body: `string[] menuIds`） |
| `GET` | `/admin/roles/{code}/menus` | 获取角色已分配的菜单 ID 列表 |

---

## DI 注册

```csharp
builder.Services.AddTenE0Menus<DemoDbContext>();
```

泛型参数 `TContext` 仅需是 `DbContext` —— 菜单表由 `TenE0SystemDbContext` 自动注册。内部实现为：

```csharp
services.TryAddScoped<IMenuService, MenuService<TContext>>();
```

---

## 设计要点

1. **物化路径而非 Adjacency List**：避免递归 CTE 查询子树，`TreePath.StartsWith(...)` 即可高效获取整棵子树。
2. **部分更新语义**：`UpdateAsync` 采用 null 即不修改的策略，前端只需传需要变更的字段。
3. **移动即子树级联**：`MoveAsync` 一次性加载子树，内存中完成 `TreePath` 和 `Level` 的批量更新，避免逐条 SQL。
4. **用户菜单祖先补齐**：`GetUserMenuTreeAsync` 在返回用户可见菜单时，自动收集所有缺失的祖先节点，保证前端菜单树不因中间节点无权限而断裂。
5. **角色分配用 diff 而非全量删除**：减少不必要的 `TenE0RoleMenu` 删除/插入操作，提升性能的同时保留审计轨迹。
