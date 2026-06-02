# 11 — 动态数据过滤规则

## 架构概览

```
管理员 ──写入 JSON 规则──→ TenE0DataFilterRule 表
                              │
                    DynamicFilterProvider (启动时 ADO.NET 加载)
                              │
                    FilterExpressionBuilder ──编译为──→ LINQ Expression
                              │
                    EF Named Query Filter (OnModelCreating 注册)
                              │
                    每次查询自动附加 WHERE 条件
```

动态数据过滤的核心流程分两个阶段：

1. **启动阶段**：`DynamicFilterProvider` 通过原始 ADO.NET 连接（绕过 DbContext，避免 `OnModelCreating` 递归）从 `DataFilterRules` 表加载所有启用的规则。`FilterExpressionBuilder` 将每条规则的 JSON 编译为 `Expression<Func<T, bool>>`，通过 `entityType.SetQueryFilter(name, filter)` 注册为 EF Named Query Filter。

2. **运行阶段**：过滤表达式通过闭包引用 `BaseDataContext` 实例的运行时属性（`CurrentUserCode`、`CurrentRoleIds` 等），EF Core 在每次查询时将最新值参数化为 SQL 参数，无需重建模型。

## 条件规则模型

### ConditionRuleGroup — 规则树节点

```csharp
public class ConditionRuleGroup
{
    public string Logic { get; set; } = "And";   // "And" | "Or"
    public List<ConditionRule> Rules { get; set; } = [];
    public List<ConditionRuleGroup> Children { get; set; } = [];  // 嵌套子组
}
```

支持递归嵌套：`Children` 中的子组与本层 `Rules` 通过本层 `Logic` 组合。例如：

```json
{
  "logic": "And",
  "rules": [
    { "field": "CreateBy", "op": "eq", "value": "{loginUser}" },
    { "field": "Status", "op": "ne", "value": "Deleted" }
  ],
  "children": [
    {
      "logic": "Or",
      "rules": [
        { "field": "OrgId", "op": "eq", "value": "{loginOrg}" },
        { "field": "IsPublic", "op": "eq", "value": "true" }
      ]
    }
  ]
}
```

等价于 SQL：`WHERE CreateBy = @user AND Status != 'Deleted' AND (OrgId IN @orgs OR IsPublic = 1)`

### ConditionRule — 单条条件

```csharp
public class ConditionRule
{
    public required string Field { get; set; }  // 实体属性名
    public required string Op { get; set; }      // 操作符
    public required string Value { get; set; }   // 比较值或占位符
}
```

### 支持的操作符

| 操作符 | 说明 | 示例 |
|--------|------|------|
| `eq` | 等于 | `{ "op": "eq", "value": "Active" }` |
| `ne` | 不等于 | `{ "op": "ne", "value": "Deleted" }` |
| `gt` | 大于 | `{ "op": "gt", "value": "1000" }` |
| `gte` | 大于等于 | `{ "op": "gte", "value": "18" }` |
| `lt` | 小于 | `{ "op": "lt", "value": "60" }` |
| `lte` | 小于等于 | `{ "op": "lte", "value": "100" }` |
| `contains` | 包含（字符串） | `{ "op": "contains", "value": "sales" }` |
| `startsWith` | 开头匹配 | `{ "op": "startsWith", "value": "BJ-" }` |
| `endsWith` | 结尾匹配 | `{ "op": "endsWith", "value": "@corp.com" }` |
| `in` | 在集合中（逗号分隔） | `{ "op": "in", "value": "Alice,Bob,Charlie" }` |
| `notIn` | 不在集合中 | `{ "op": "notIn", "value": "dept1,dept2" }` |

### 占位符

运行时由 `FilterExpressionBuilder` 自动替换为 `BaseDataContext` 实例属性：

| 占位符 | 解析目标 | 类型 |
|--------|---------|------|
| `{loginUser}` | `CurrentUserCode` | `string?` |
| `{loginRole}` | `CurrentRoleIds` | `string[]` |
| `{loginOrg}` | `CurrentOrgIds` | `string[]` |

典型用法：实现行级数据隔离。

```json
// 只能看自己创建的数据
{ "field": "CreateBy", "op": "eq", "value": "{loginUser}" }

// 只能看本组织的数据
{ "field": "OrgId", "op": "in", "value": "{loginOrg}" }

// 只能看自己角色有权限的数据
{ "field": "RoleId", "op": "in", "value": "{loginRole}" }
```

## AND 组合：三条过滤通道

动态过滤与静态过滤在 `BaseDataContext.OnModelCreating` 中 AND 组合，同一条 SQL 查询同时生效：

```
SELECT ... FROM Orders
WHERE SoftDelete = 0                    -- ① 软删除过滤
  AND (BypassFilters=1 OR OrgId=@org)   -- ② DataPrivilege 静态规则
  AND (BypassFilters=1 OR Amount>1000)  -- ③ 动态过滤规则
```

### 1. SoftDelete（软删除）

所有实现 `ISoftDeleteEntity` 接口的实体自动注册 `SoftDelete` 过滤器，等价于 `WHERE IsSoftDelete = false`。

### 2. DataPrivilege（代码定义的行级权限）

通过 `IEntityFilterContributor` 实现，编译时确定，运行时通过 `BaseDataContext` 属性求值。例如按组织隔离：

```csharp
protected override Expression<Func<DemoEntity, bool>>? Build(BaseDataContext context) =>
    entity => context.BypassFilters
           || !context.IsAuthenticated
           || entity.OrgId == null
           || entity.OrgId == context.CurrentOrgId;
```

### 3. DynamicFilter（运行时配置规则）

管理员通过 Admin API 写入 JSON 规则，启动时自动加载为 Named Query Filter。

## BypassFilters 短路

所有过滤表达式（DataPrivilege + DynamicFilter）最外层自动包装 `BypassFilters` 短路：

```csharp
// FilterExpressionBuilder 自动添加
var bypassExpr = Expression.Property(contextExpr, nameof(BaseDataContext.BypassFilters));
var finalBody = Expression.OrElse(bypassExpr, groupBody);
```

`BypassFilters` 来自 `IDataAccessPolicy.BypassFilters`，当用户角色包含 `super_admin` 等超管角色时返回 `true`，SQL 翻译为 `WHERE @bypass=1 OR (...原始条件...)`。

## 规则管理 API

### IDataFilterRuleService

```csharp
public interface IDataFilterRuleService
{
    Task<IReadOnlyList<TenE0DataFilterRule>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TenE0DataFilterRule>> GetByEntityAsync(string entityTypeName, CancellationToken ct = default);
    Task<TenE0DataFilterRule?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<TenE0DataFilterRule> CreateAsync(DataFilterRuleCreateRequest request, CancellationToken ct = default);
    Task UpdateAsync(string id, DataFilterRuleUpdateRequest request, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task SetEnabledAsync(string id, bool enabled, CancellationToken ct = default);
}
```

### Admin Minimal API 端点

```csharp
// 获取全部规则
app.MapGet("/admin/data-filters", ...);

// 获取单条规则
app.MapGet("/admin/data-filters/{id}", ...);

// 查询指定实体的规则
app.MapGet("/admin/data-filters/entity/{entityTypeName}", ...);

// 创建规则
app.MapPost("/admin/data-filters", ...);

// 更新规则（部分更新，null 字段表示不修改）
app.MapPut("/admin/data-filters/{id}", ...);

// 删除规则
app.MapDelete("/admin/data-filters/{id}", ...);

// 启用/禁用
app.MapPatch("/admin/data-filters/{id}/toggle", ...);
```

### 创建规则请求示例

```json
// POST /admin/data-filters
{
  "entityTypeName": "MyApp.Entities.Order",
  "ruleJson": "{\"logic\":\"And\",\"rules\":[{\"field\":\"Amount\",\"op\":\"gte\",\"value\":\"1000\"}]}",
  "description": "只看金额 >= 1000 的订单",
  "isEnabled": true
}
```

### 部分更新请求

```json
// PUT /admin/data-filters/{id}
{
  "ruleJson": "{\"logic\":\"And\",\"rules\":[{\"field\":\"Amount\",\"op\":\"gte\",\"value\":\"5000\"}]}",
  "description": null,       // null 表示不修改描述
  "isEnabled": false         // 禁用原规则
}
```

## DI 注册

```csharp
// Program.cs
builder.Services.AddTenE0DynamicFilters<DemoDbContext>();
```

内部注册：

| 服务 | 生命周期 | 职责 |
|------|---------|------|
| `IDynamicFilterProvider` | Singleton | 缓存规则，在 `OnModelCreating` 时注册 Named Query Filter |
| `IDataFilterRuleService` | Scoped | 规则 CRUD 管理（每请求独立事务） |

### 启动时加载规则

```csharp
// Program.cs — 在 app.Build() 之后执行
using (var scope = app.Services.CreateScope())
{
    var filterProvider = scope.ServiceProvider.GetRequiredService<IDynamicFilterProvider>();
    var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DemoDbContext>>();
    using var ctx = await contextFactory.CreateDbContextAsync();
    var providerName = ctx.Database.ProviderName ?? "";
    var connStr = ctx.Database.GetConnectionString() ?? "";
    // 将 EF provider name 映射为 ADO.NET provider name 后调用
    await filterProvider.LoadRulesAsync(connStr, adoProvider);
}
```

## 完整 JSON 规则示例

### 场景 1：按用户 + 组织隔离

```json
{
  "logic": "And",
  "rules": [
    { "field": "CreateBy", "op": "eq", "value": "{loginUser}" }
  ],
  "children": [
    {
      "logic": "Or",
      "rules": [
        { "field": "OrgId", "op": "in", "value": "{loginOrg}" },
        { "field": "IsCrossOrg", "op": "eq", "value": "true" }
      ]
    }
  ]
}
```

### 场景 2：金额范围 + 角色可见

```json
{
  "logic": "And",
  "rules": [
    { "field": "Amount", "op": "gte", "value": "10000" },
    { "field": "Status", "op": "ne", "value": "Cancelled" }
  ],
  "children": [
    {
      "logic": "Or",
      "rules": [
        { "field": "SalesPerson", "op": "eq", "value": "{loginUser}" },
        { "field": "ManagerRole", "op": "in", "value": "{loginRole}" }
      ]
    }
  ]
}
```

### 场景 3：仅查看活跃记录

```json
{
  "logic": "And",
  "rules": [
    { "field": "IsDeleted", "op": "eq", "value": "false" },
    { "field": "ExpireAt", "op": "gt", "value": "{loginUser}" }
  ]
}
```

## ⚠️ 重要限制

- **修改规则后需重启应用**：EF Model 在首次使用时缓存（`IModelCacheKeyFactory`），新增/修改规则后不会自动生效。这是当前版本的一个已知限制。如需动态生效，可参考实现自定义 `IModelCacheKeyFactory` 使规则变更时触发模型缓存失效。
- **InMemory 数据库跳过加载**：`DynamicFilterProvider.LoadRulesAsync` 在连接失败时 graceful 降级为空规则集，不会阻塞启动。
- **类型自动转换**：`FilterExpressionBuilder.ConvertValue` 支持 `string/int/long/Guid/DateTime/DateTimeOffset/bool/decimal/float/double/enum` 及对应的 `Nullable<T>` 类型，不支持的属性类型会抛出 `NotSupportedException`。
