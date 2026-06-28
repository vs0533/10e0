# 28 — 实体读侧查询服务 `IEntityQueryService`

`IEntityQueryService` 是 [IEntityService（写侧）](06-entity-service.md) 的**读侧对称** —— 为 CQRS 读路径提供官方推荐入口,统一分页 / 筛选 / 排序 / 投影的写法,自动复用 EF Named Query Filter(软删除 + 行级权限 + 租户)。

> 💡 **设计动机**:`IEntityService` 只有 CUD(Create/Update/Delete),没有 R(Read)。业务 Handler 要查询时只能每个列表页手写一遍 LINQ 分页/排序/筛选。`IEntityQueryService` 把这套横切逻辑收敛到一个服务,让"读"也有官方推荐姿势。
>
> 它**不强制**使用 —— 复杂 join / 聚合统计仍由业务方自己写;它只是列表 / 详情 / 计数这些高频读场景的工具。

---

## 1. 概述

一个接口覆盖所有实体的常见读操作,核心能力:

- **分页 / 列表 / 详情 / 计数 / 存在性**:`PagedAsync` / `ListAsync` / `GetByIdAsync` / `CountAsync` / `ExistsAsync`
- **投影到 View DTO**:每个方法都有 `<TEntity, TView>` 重载,详情页 / 列表页直接出 View
- **自动复用 Named Query Filter**:无需手写 `Where(IsSoftDelete==false)`,软删除 / 行级权限 / 租户过滤器由 EF 自动附加
- **显式旁路开关**:`EntityReadOptions.BypassFilters` 提供**细粒度**旁路(取代危险的 `IgnoreQueryFilters()` 全量旁路)
- **声明式筛选 / 排序**:`ReadFilter` / `ReadOrderBy` 结构化对象,字段名经运行时白名单校验,**防表达式注入**
- **AsNoTracking 默认开**:读场景通常不跟踪,降低内存占用

---

## 2. DI 注册

```csharp
builder.Services.AddTenE0EntityService();  // 注册 IEntityService + IEntityQueryService (Scoped)
```

`IEntityQueryService` 随 `AddTenE0EntityService` 注册(与 `IEntityService` 对称),已在 `AddTenE0All` 的**基础套件(始终启用)**内,**零 opt-in 开关**。消费者注入即可用:

```csharp
public sealed class MyHandler(IDbContextFactory<AppDbContext> dcFactory, IEntityQueryService querySvc)
    : ICommandHandler<MyQuery, PagedResult<MyView>> { ... }
```

---

## 3. 核心方法

### 分页查询(列表页最常用)

```csharp
var result = await querySvc.PagedAsync<Product, ProductView>(
    dc,
    query: new PagedQuery(Page: 1, PageSize: 20),
    selector: p => new ProductView(p.Id, p.Code, p.Name, p.Price),
    options: new EntityReadOptions
    {
        Filters = [new ReadFilter("Name", ReadOperator.Contains, keyword)],
        OrderBy = [new ReadOrderBy("CreateTime", Descending: true)],
    },
    cancellationToken: ct);

// result.Items / result.Total / result.TotalPages —— 行级权限/软删除/租户由 EF 自动附加
```

返回 [`PagedResult<T>`](16-dynamic-queries.md#pagedresultt),与框架统一分页响应一致。

### 详情(按主键 + 投影)

```csharp
var view = await querySvc.GetByIdAsync(dc, id, p => new ProductView(p.Id, p.Code, p.Name));
// 被行级过滤掉的行返回 null(与"不存在"语义一致)
```

### 列表 / 计数 / 存在性

```csharp
var all = await querySvc.ListAsync<Product>(dc);
var count = await querySvc.CountAsync<Product>(dc);
var exists = await querySvc.ExistsAsync<Product>(dc, id);
```

完整方法签名见 [`IEntityQueryService`](https://github.com/vs0533/10e0/blob/dev/src/10E0.Core/EntityService/IEntityQueryService.cs)。

---

## 4. `EntityReadOptions` —— 读选项

与 [`EntityWriteOptions`](06-entity-service.md) 对称的不可变选项对象:

| 属性 | 类型 | 说明 |
|------|------|------|
| `Filters` | `IReadOnlyList<ReadFilter>?` | 筛选条件(字段白名单)。null 时不附加任何 Where(仅靠 EF Named Filter) |
| `OrderBy` | `IReadOnlyList<ReadOrderBy>?` | 排序:属性名 + 方向,多个时按顺序组合 |
| `BypassFilters` | `IReadOnlySet<string>?` | 显式旁路过滤器(见 §6) |
| `AsNoTracking` | `bool`(默认 true) | 读场景通常不跟踪 |

### `ReadFilter` —— 类型安全的筛选条件

```csharp
public sealed record ReadFilter(string Field, ReadOperator Operator, object? Value);
```

`Operator` 支持 `Eq`/`Ne`/`Gt`/`Gte`/`Lt`/`Lte`/`Contains`/`StartsWith`/`EndsWith`/`In`。

```csharp
new ReadFilter("Price", ReadOperator.Gte, 100m)
new ReadFilter("Name", ReadOperator.Contains, "test")
new ReadFilter("Code", ReadOperator.In, new[] { "C001", "C002" })  // 值须为 IEnumerable(非 string)
```

### `ReadOrderBy` —— 排序项

```csharp
new ReadOrderBy("CreateTime", Descending: true)   // CreateTime desc
new ReadOrderBy("Name")                            // Name asc (默认)
```

---

## 5. ⚠️ 安全 —— 字段白名单与表达式注入

`ReadFilter.Field` 和 `ReadOrderBy.Field` 在**运行时经 EF 模型白名单校验**:必须是实体在 `DbContext` 中注册的真实属性名(反射 + 缓存),否则抛 `ArgumentException`。这从根本上杜绝了 [`DynamicWhere` 表达式注入](16-dynamic-queries.md#表达式注入风险需注意-)的隐患:

```
// ❌ 攻击:试图读敏感字段 / 绕过软删 —— 直接被拒绝
new ReadFilter("Password", ReadOperator.Eq, "secret")  // ArgumentException: 非法筛选字段
```

> 这是 `IEntityQueryService` 相对 [`DynamicQueryExtensions`](16-dynamic-queries.md) 的核心安全升级:动态查询需要业务方自己写白名单校验(见 [16 章安全章节](16-dynamic-queries.md#表达式注入风险需注意-)),而读服务**默认就是安全的**。

非法字段抛 `ArgumentException`(编程错误,不入 `IErrs` —— 与 `[Sequence]` 字段缺失这类编程错误一致)。错误处理仍由 [`TenE0ExceptionHandler`](#) 集中映射为 500。

---

## 6. ⚠️ 安全 —— `BypassFilters` 三态

显式旁路 Named Query Filter,**取代危险的 `IgnoreQueryFilters()` 全量旁路**:

| 取值 | 行为 | 适用场景 |
|------|------|---------|
| `null` / 空(默认) | 应用**全部**过滤器(软删 + 行级权限 + 租户) | 99% 业务查询 —— **最安全** |
| `["Tenant"]` 等具体名 | 仅旁路这些命名过滤器,其余继续生效 | 管理后台跨租户审计(仍排除软删) |
| `["*"]` | **全量**旁路(等价无参 `IgnoreQueryFilters()`)+ 日志告警 | 仅受控场景(如数据迁移),误用有可见告警 |

```csharp
// 管理后台审计:跨租户可见,但软删 / 行级权限仍生效
var all = await querySvc.ListAsync<AssetDoc>(dc, new EntityReadOptions
{
    BypassFilters = new HashSet<string> { "Tenant" },
});

// ⚠️ 全量旁路(慎用,会记 Warning)
var everything = await querySvc.ListAsync<AssetDoc>(dc, new EntityReadOptions
{
    BypassFilters = new HashSet<string> { "*" },
});
```

已知命名过滤器名称:`SoftDelete`(软删除)/ `Tenant`(多租户)/ `DataPrivilege:<ContributorType>`(行级权限,见 [`IEntityFilterContributor`](09-permissions.md#行级权限-dataprivilege))。

> 💡 **与超管短路的关系**:行级权限 / 租户过滤器的表达式内部已 `|| context.BypassFilters` 短路(见 [BaseDataContext](07-data-context.md)),所以**超管不需要显式传 `BypassFilters`** —— `IDataAccessPolicy.BypassFilters=true` 自动生效。`BypassFilters` 是给**非超管**在受控场景细粒度旁路用的。

---

## 7. 与 `IEntityService` 的对称关系

| 维度 | `IEntityService`(写) | `IEntityQueryService`(读) |
|------|----------------------|---------------------------|
| 方法 | `CreateAsync` / `UpdateAsync` / `DeleteAsync` | `GetByIdAsync` / `ListAsync` / `PagedAsync` / `CountAsync` / `ExistsAsync` |
| 选项 | `EntityWriteOptions` | `EntityReadOptions` |
| DbContext | 调用方传入 | 调用方传入 |
| 生命周期 | Scoped | Scoped |
| 横切关注点 | 唯一性 / 字段权限 / 流水号 / 审计 / M:N | Named Query Filter(软删/行级权限/租户) / 白名单 / AsNoTracking |
| DI 注册 | `AddTenE0EntityService`(基础套件) | 同左(零 opt-in) |

两者**职责清晰分离**:写走 EntityService(审计字段、唯一性、权限),读走 EntityQueryService(过滤、投影、分页)。读不触发审计、不校验唯一性;写不投影、不分页。

---

## 8. 何时用 `IQueryHandler` vs 直接调服务

`IEntityQueryService` **不替代** `IQueryHandler<T>` —— CQRS 查询命令仍由业务 Handler 实现,服务只是 Handler **内部可用的工具**:

```csharp
// ✅ 推荐:Query 声明在 record 上,Handler 用读服务实现
[RequirePermission(ProductPermissions.View)]
public sealed record ListProductsQuery(PagedQuery Paged, string? Name) : IQuery<PagedResult<ProductView>>;

public sealed class ListProductsHandler(
    IDbContextFactory<AppDbContext> dcFactory,
    IEntityQueryService querySvc)
    : ICommandHandler<ListProductsQuery, PagedResult<ProductView>>
{
    public async Task<PagedResult<ProductView>> HandleAsync(ListProductsQuery q, CancellationToken ct)
    {
        await using var dc = await dcFactory.CreateDbContextAsync(ct);
        return await querySvc.PagedAsync<Product, ProductView>(dc, q.Paged,
            selector: p => new ProductView(p.Id, p.Code, p.Name),
            options: new EntityReadOptions { /* ... */ }, ct);
    }
}
```

**直接调服务**(不经 Handler)的场景:统计任务、后台 Job、管理后台一次性查询。此时权限检查要由调用方自己保证(`IEntityQueryService` 不做权限)。

复杂场景(多表 join / 聚合统计 / 自定义 SQL)仍由业务方手写 LINQ —— 读服务是高频简单读的快捷方式,不是 ORM 替代品。

---

## 9. 范本

`src/10E0.Api/` 提供端到端范本(克隆即可用):

| 看什么 | 路径 |
|--------|------|
| 分页 Query + 权限声明 | `src/10E0.Api/Handlers/DemoCommands.cs`(`PagedDemosQuery`) |
| 读服务范本 Handler | `src/10E0.Api/Handlers/PagedDemosQueryHandler.cs` |
| 分页端点 | `src/10E0.Api/Endpoints/DemoEndpoints.cs`(`GET /demo/paged`) |
| 单元测试 | `tests/10E0.Core.Tests/EntityService/EntityQueryServiceTests.cs` |
| 集成测试(过滤效果) | `tests/10E0.Core.Tests/EntityService/EntityQueryAcceptanceTests.cs` |

---

## 10. 完整示例 —— 列表页 + 详情页 + 统计

```csharp
// 列表页(带搜索)
var page = await querySvc.PagedAsync<Product, ProductView>(
    dc, new PagedQuery(1, 20),
    p => new ProductView(p.Id, p.Code, p.Name, p.Price),
    new EntityReadOptions
    {
        Filters = keyword is null ? null : [new ReadFilter("Name", ReadOperator.Contains, keyword)],
        OrderBy = [new ReadOrderBy("CreateTime", Descending: true)],
    });

// 详情页
var detail = await querySvc.GetByIdAsync(dc, id, p => new ProductView(p.Id, p.Code, p.Name, p.Price));
if (detail is null) return Results.NotFound();

// 统计(自动排除软删 / 隔离租户)
var totalActive = await querySvc.CountAsync<Product>(dc, new EntityReadOptions
{
    Filters = [new ReadFilter("IsActive", ReadOperator.Eq, true)],
});

// 管理后台审计:跨租户但排除软删
var auditList = await querySvc.ListAsync<AssetDoc>(dc, new EntityReadOptions
{
    BypassFilters = new HashSet<string> { "Tenant" },
});
```

---

## 11. 相关文档

- [06 — EntityService(写侧)](06-entity-service.md) —— 读服务的写侧对称
- [16 — 动态查询](16-dynamic-queries.md) —— 裸扩展方法(`DynamicWhere` / `Page`),读服务内部复用它们
- [07 — DataContext](07-data-context.md) —— Named Query Filter 的注册与 `BypassFilters` 短路
- [09 — 权限系统](09-permissions.md) —— 行级权限(`IEntityFilterContributor`)由读服务自动复用
- [20 — 多租户](20-multi-tenancy.md) —— `Tenant` 命名过滤器由读服务自动复用
