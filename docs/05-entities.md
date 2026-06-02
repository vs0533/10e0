# 5 — 实体模型

## 概述

10E0 提供了一套完整的实体基类层次，通过标记接口驱动行为。框架自动处理审计字段填充、软删除、流水号生成，开发者只需继承合适的基类即可。

## 实体继承链

```
IBaseEntity                     ← string Id
    ↑
BaseEntity (abstract)           ← Id = Guid.NewGuid().ToString("N")
    ↑
ITimerEntity → TimedEntity     ← + CreateTime/CreateBy/UpdateTime/UpdateBy
    ↑
ISoftDeleteEntity → AuditedEntity ← + IsSoftDelete/DeleteTime/DeleteBy 【最常用】
    ↑
ITreeEntity → TreeAuditedEntity  ← + ParentId（树形结构）
    ↑
AggregateRoot                    ← + Raise(IDomainEvent) 领域事件
```

## 各基类说明

### BaseEntity — 仅主键

```csharp
public class Tag : BaseEntity
{
    public string Name { get; set; } = "";
}
```

- `string Id` — 32 位无分隔符 GUID
- 适用场景：简单实体如配置表、字典表

### TimedEntity — 自动审计时间戳

```csharp
public class LogEntry : TimedEntity
{
    public string Message { get; set; } = "";
}
```

- `DateTimeOffset? CreateTime`, `string? CreateBy`
- `DateTimeOffset? UpdateTime`, `string? UpdateBy`
- 由 `AuditInterceptor` 在 SaveChanges 时自动填充

### AuditedEntity — 审计 + 软删除 【最常用】

```csharp
public class Product : AuditedEntity
{
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}
```

- 继承 TimedEntity 所有字段
- `bool IsSoftDelete`, `DateTimeOffset? DeleteTime`, `string? DeleteBy`
- 调用 `Remove()` 时自动转为软删除（审计拦截器处理）

### TreeAuditedEntity — 审计 + 软删除 + 树形结构

```csharp
public class Category : TreeAuditedEntity
{
    public string Name { get; set; } = "";
}
```

- 继承 AuditedEntity 所有字段
- `string? ParentId` — 父节点 ID
- 适用场景：组织架构、菜单、分类

### AggregateRoot — 审计 + 软删除 + 领域事件

```csharp
public class Order : AggregateRoot
{
    public string CustomerName { get; set; } = "";
    public decimal Total { get; set; }

    public void Confirm()
    {
        Raise(new OrderConfirmedEvent(Id, Total));
    }
}
```

- 继承 AuditedEntity 所有字段
- `protected void Raise(IDomainEvent)` — 触发领域事件
- `IReadOnlyList<IDomainEvent> PendingEvents` — 待发布事件
- 适用场景：需要领域事件的业务聚合根

## 标记接口体系

| 接口 | 属性 | 被谁处理 |
|------|------|----------|
| `IBaseEntity` | `string Id` | EF Core 主键映射 |
| `ITimerEntity : IBaseEntity` | CreateTime/CreateBy/UpdateTime/UpdateBy | `AuditInterceptor` 自动填充 |
| `ISoftDeleteEntity : IBaseEntity` | IsSoftDelete/DeleteTime/DeleteBy | `AuditInterceptor` 转软删除 + 查询过滤器 |
| `ITreeEntity : IBaseEntity` | `string? ParentId` | `OrgTreeService` / `MenuService` 处理 |

## 软删除机制

调用 `DbSet.Remove()` 或 `DbContext.Remove()` 时：

1. `AuditInterceptor` 将 `EntityState.Deleted` 转为 `EntityState.Modified`
2. 设置 `IsSoftDelete = true`、`DeleteTime = now`
3. 查询时自动过滤：`WHERE IsSoftDelete = false`

**绕过软删除过滤**：

> ⚠️`IgnoreQueryFilters()` 会移除**所有**命名过滤器（SoftDelete + DataPrivilege + DynamicFilter），属安全敏感操作。仅在管理后台等受控场景使用。

```csharp
// 绕过全部过滤器（含行级权限！），仅在受控场景使用
var all = await dc.Set<Product>().IgnoreQueryFilters().ToListAsync();
```

## 扩展框架用户实体

```csharp
public class AppUser : TenE0User
{
    public string? Avatar { get; set; }
    public string? Department { get; set; }
    public DateOnly? Birthday { get; set; }
}

// DbContext 中指定泛型
public class AppDbContext : TenE0SystemDbContext<AppUser, TenE0Role>
{
    public AppDbContext(DbContextOptions options,
        ICurrentUserContext currentUser,
        IDataAccessPolicy accessPolicy,
        IEnumerable<IEntityFilterContributor> filters,
        IDynamicFilterProvider dynamicFilterProvider)
        : base(options, currentUser, accessPolicy, filters, dynamicFilterProvider) { }
}

// DI 注册
builder.Services.AddTenE0Identity<AppUser, AppDbContext>(opt => { ... });
```

EF Core 通过 TPH（Table Per Hierarchy）同表存储 `TenE0User` 及其所有子类。

## M:N 关系

不再需要 `MultipleEntity` 基类。直接使用 EF Core 的 Skip Navigation：

```csharp
public class Student : AuditedEntity
{
    public ICollection<Course> Courses { get; set; } = [];
}

public class Course : AuditedEntity
{
    public ICollection<Student> Students { get; set; } = [];
}
```

EntityService 的 `RelationProcessor` 自动对 M:N 关系做 diff 处理。

## 审计字段保护

以下字段被框架硬锁保护，任何通过 `EntityService` 的写操作都不会修改：

`Id`、`CreateTime`、`CreateBy`、`UpdateTime`、`UpdateBy`、`IsSoftDelete`、`DeleteTime`、`DeleteBy`

如需手动设置，请直接使用 DbContext 而不通过 EntityService。
