# DataContext/ — EF Core DbContext 基类

## 文件说明

| 文件 | 职责 |
|------|------|
| `BaseDataContext.cs` | 所有 DbContext 的抽象基类。注册 Named Query Filters（SoftDelete + DataPrivilege），暴露 `CurrentUserCode`/`CurrentRoleIds`/`BypassFilters` 等属性供过滤表达式引用 |
| `TenE0SystemDbContext.cs` | 系统级 DbContext 基类。提供所有框架表的 DbSet（User/Role/Menu/Org/Sequence/Outbox/File/DataFilterRule 等），`OnModelCreating` 调用所有 `ConfigureTenE0XxxTables` 扩展 |

## Named Query Filters

在 `OnModelCreating` 中自动注册：

| Filter 名称 | 条件 | 触发条件 |
|-------------|------|----------|
| `SoftDelete` | `e.IsSoftDelete == false` | 实体实现 `ISoftDeleteEntity` |
| `DataPrivilege:Xxx` | 由 `IEntityFilterContributor` 定义 | 实体匹配 contributor 的泛型参数 |

多个 Filter 在同实体上 AND 组合。绕过过滤：`modelBuilder.Entity<T>().IgnoreQueryFilters(["SoftDelete"])`

## 运行时属性

```csharp
context.CurrentUserCode    // 当前用户编码（来自 ICurrentUserContext）
context.CurrentRoleIds     // 当前角色 ID 列表
context.CurrentOrgIds      // 当前组织 ID 列表
context.IsAuthenticated    // 是否已认证
context.BypassFilters      // 是否绕过所有行级过滤（超管场景）
```

## 子目录

| 目录 | 职责 |
|------|------|
| `Interceptors/` | EF Core SaveChanges 拦截器 |
