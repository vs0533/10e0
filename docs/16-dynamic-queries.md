# 16 — 动态查询

基于 `System.Linq.Dynamic.Core` 的运行时动态查询支持，允许通过字符串表达式构建 LINQ 查询，主要服务于 REST API 的通用查询接口。

> ⚠️ **安全警告**：`DynamicWhere` 的表达式字符串本身是用户可控的，直接透传可导致表达式注入——绕过软删除、行级权限等 Named Query Filters，或读取敏感字段。详见下方 [表达式注入风险](#表达式注入风险) 章节。

## 扩展方法

所有扩展方法定义在 `TenE0.Core.Queries.DynamicQueryExtensions` 中，作用于 `IQueryable<T>`。

### DynamicWhere

运行时动态 WHERE 条件。**必须使用参数化占位符** `@0`、`@1` 等传值，禁止字符串拼接。

```csharp
// ✅ 正确 — 参数化
query.DynamicWhere("Name.Contains(@0) && Amount > @1", "test", 100m);

// ❌ 错误 — 字符串拼接，SQL 注入风险
query.DynamicWhere($"Name.Contains(\"{input}\")");  // 禁止
```

占位符可以是 `@0`/`@1` 或 `@p0`/`@p1` 格式。传入 `null` 或空字符串时直接返回原查询。

### DynamicOrderBy

指定排序字段和方向，支持多字段：

```csharp
query.DynamicOrderBy("CreateTime desc, Name asc");  // 多字段排序
query.DynamicOrderBy("Amount desc");                // 单字段降序
```

格式：`propertyName [asc|desc]`，多字段用逗号分隔。

### DynamicSelect

动态投影至匿名类型。返回 `IQueryable`（非泛型），适用于只需要部分字段的场景。

```csharp
query.DynamicSelect("new (Id, OrderNo, Amount as Price)");
```

### DynamicGroupBy

动态分组 + 聚合。`keySelector` 指定分组字段，`resultSelector` 指定输出形状。

```csharp
query.DynamicGroupBy("Status", "new (Key as GroupKey, Count() as Total, Sum(Amount) as SumAmount)");
```

### Page

安全分页，内部强制上限保护：

```csharp
query.Page(page: 1, pageSize: 20);
```

- `page < 1` 自动修正为 1
- `pageSize < 1` 自动修正为 10
- `pageSize > 1000` **强制截断为 1000**（安全上限，防止恶意大分页攻击）

### WhereIf

条件过滤——仅在条件满足时附加 WHERE，典型用于可选搜索参数：

```csharp
query.WhereIf(!string.IsNullOrEmpty(keyword), "Name.Contains(@0)", keyword);
query.WhereIf(minPrice.HasValue, "Amount >= @0", minPrice);
query.WhereIf(categoryId.HasValue, "CategoryId == @0", categoryId);
```

## 分页 DTO

### PagedQuery

请求参数 DTO，支持模型绑定：

```csharp
public record PagedQuery(
    int Page = 1,         // 页码，从 1 开始
    int PageSize = 20,    // 每页条数
    string? OrderBy = null, // 排序表达式
    string? Where = null   // 动态 WHERE 表达式
);
```

### PagedResult\<T\>

统一分页响应：

```csharp
public record PagedResult<T>(
    IReadOnlyList<T> Items,  // 当前页数据
    int Total,               // 总记录数
    int Page,                // 当前页码
    int PageSize,            // 每页条数
    int TotalPages           // 总页数（由 Create 自动计算）
);
```

使用 `PagedResult<T>.Create(items, total, page, pageSize)` 工厂方法构建。

## ⚠️ 表达式注入风险

### SQL 注入：已防护 ✅

`DynamicWhere` 所有**值参数**通过 `@0`/`@1` 参数化传入，`System.Linq.Dynamic.Core` 在内部将其编译为 `ParameterExpression` 节点，由 EF Core 翻译为参数化 SQL，从根本上杜绝 SQL 注入。

### 表达式注入：需注意 ⚠️

**WHERE 表达式字符串本身是用户可控的**（如从查询参数直接传入），攻击者可构造恶意表达式绕过软删除、行级权限等 Named Query Filters，或读取敏感字段：

```
?where=Password.Contains(@0)      # 读取敏感字段
?where=IsSoftDelete==false||true  # 绕过软删除过滤
```

**生产环境必须限制可查询的字段和操作符**，推荐采用以下方案之一：

1. **白名单字段** — 校验 expression 中只包含允许的属性名：
```csharp
var allowedFields = new[] { "Name", "Amount", "CreateTime", "Status" };
if (!allowedFields.All(f => query.Where!.Contains(f)))
    return Results.BadRequest(new { error = "包含不允许的查询字段" });
```

2. **预定义查询模板** — 不允许用户自由输入表达式，只允许选择预定义过滤条件：
```csharp
// 前端传 filterKey，后端映射为安全表达式
var whereMap = new Dictionary<string, string> {
    ["byName"] = "Name.Contains(@0)",
    ["byAmount"] = "Amount >= @0",
};
q.WhereIf(whereMap.TryGetValue(filterKey, out var expr), expr!, value);
```

3. **EntityService 封装** — 在通用查询服务层做统一校验，避免每个 Handler 重复处理。

## DI 注册

**无需注册**。扩展方法直接作用于 `IQueryable<T>`，引入命名空间 `TenE0.Core.Queries` 即可使用。

## 完整示例

```csharp
// GET /demo/query?where=Name.Contains("test")&orderBy=CreateTime desc&page=1&pageSize=20

app.MapGet("/demo/query", async (
    AppDbContext db,
    [AsParameters] PagedQuery query,
    CancellationToken ct) =>
{
    var q = db.Orders.AsQueryable();

    // ⚠️ 生产环境禁止直接透传用户输入 — 见上方"表达式注入风险"章节
    // 此处仅为演示，实际使用时必须对 query.Where 做白名单校验或使用预定义模板
    q = q.WhereIf(!string.IsNullOrEmpty(query.Where), query.Where!);

    // 动态排序（默认按 CreateTime 降序）
    q = q.DynamicOrderBy(query.OrderBy ?? "CreateTime desc");

    // 获取总数 + 分页数据
    var total = await q.CountAsync(ct);
    var items = await q.Page(query.Page, query.PageSize).ToListAsync(ct);

    return PagedResult<Order>.Create(items.AsReadOnly(), total, query.Page, query.PageSize);
});
```

实际项目中建议在 **EntityService** 层封装通用查询逻辑，避免在每个 Handler 中重复。
