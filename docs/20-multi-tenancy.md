# 多租户（Multi-Tenancy）

> 实现版本：#11（已实现）
> 关联：`src/10E0.Core/Abstractions/EntityContracts.cs` `IMultiTenantEntity`、 `src/10E0.Core/Abstractions/ITenantContext.cs` `ITenantContext`、 `src/10E0.Core/Auth/HttpTenantContext.cs` `HttpTenantContext`、 `src/10E0.Core/DataContext/BaseDataContext.cs` `BaseDataContext`

## 概述

10E0 框架内置**基于行级标记的租户隔离**：业务实体实现 `IMultiTenantEntity` 后，
`BaseDataContext` 在 `OnModelCreating` 时自动为该实体注册名为 `Tenant` 的 Named Query Filter，
跨租户查询无需业务方手写条件，EF Core 自动追加 `TenantId == currentTenantId` 谓词。

## 核心组件

| 组件 | 命名空间 | 职责 |
|------|----------|------|
| `IMultiTenantEntity` | `TenE0.Core.Abstractions` | 实体标记接口，声明 `string TenantId` 属性 |
| `ITenantContext` | `TenE0.Core.Abstractions` | 当前租户上下文抽象（HTTP / Ambient） |
| `HttpTenantContext` | `TenE0.Core.Auth` | HTTP 实现：从 JWT `tenant_id` claim 读取 |
| `JwtClaims.TenantId` | `TenE0.Core.Abstractions` | JWT Claim 常量 `"tenant_id"` |
| `BaseDataContext.CurrentTenantId` | `TenE0.Core.DataContext` | 暴露给过滤表达式动态引用 |

## 业务方接入步骤

### 1. 让业务实体实现 `IMultiTenantEntity`

```csharp
using TenE0.Core.Abstractions;

public class Course : IBaseEntity, IMultiTenantEntity, ITimerEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string TenantId { get; set; } = string.Empty;   // 必填
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset? CreateTime { get; set; }
    public string? CreateBy { get; set; }
    // ... 其他业务字段
}
```

注册到 `DbContext`：

```csharp
public class AppDbContext : TenE0SystemDbContext<AppUser, TenE0Role>
{
    public DbSet<Course> Courses => Set<Course>();
}
```

之后所有 `dc.Courses.Where(...)` 都会自动被 EF 追加 `&& TenantId == currentTenantId` 谓词。

### 2. 用户实体携带 TenantId

`TenE0User` 已默认带 `string? TenantId` 字段（nullable 以兼容多租户关闭场景）：

```csharp
public class AppUser : TenE0User
{
    public string? TenantId { get; set; }   // 来自 TenE0User，无需重写
}
```

用户在 admin 后台被分配租户，登录时 `LoginCommandHandler` 自动把 `user.TenantId` 写入 JWT
`tenant_id` claim。

### 3. HTTP 上下文自动解析

`HttpTenantContext` 由 `AddTenE0Core()` 自动注册（Scoped，零配置），
从 `HttpContext.User.FindFirst("tenant_id")` 读值：

- 有 claim 且非空 → 透传给 EF Filter
- 无 claim / 空字符串 / 未认证 → 返回 `null` → EF Filter 走"安全默认"（隐藏所有租户行）

### 4. 超管旁路

`IDataAccessPolicy.BypassFilters == true` 时 EF Filter 短路可见全部租户。
框架的 `DefaultDataAccessPolicy` 始终返回 `false`；`Permissions` 模块注册的实现会读
`PermissionsOptions.SuperUserCodes`，对超管角色返回 `true`。

```csharp
// 手动检查当前是否在 bypass 模式
if (dc.BypassFilters) { /* 跨租户审计 / 运营支持 */ }
```

## 显式旁路租户过滤

```csharp
// 跨租户审计场景：需要看到所有租户
var allDocs = dc.Documents
    .IgnoreQueryFilters("Tenant")   // 显式旁路 Tenant filter（不影响 SoftDelete / DataPrivilege）
    .ToList();
```

`IgnoreQueryFilters("Tenant")` 只旁路 `Tenant` 这一个命名过滤器，其他过滤器（SoftDelete、
DataPrivilege）继续生效。`IgnoreQueryFilters()`（不带参数）旁路该实体**全部**命名过滤器。

## 手动赋值 TenantId

`IMultiTenantEntity` 不强制 `TenantId` 在编译期非空（`string` 而非 `string?`），但
**业务方必须在 Insert 之前赋值**，否则 EF Filter 会让该行对所有租户不可见：

```csharp
var course = new Course
{
    Id = Guid.NewGuid().ToString(),
    TenantId = currentTenantContext.TenantId!,  // 从 ITenantContext 注入的当前请求租户
    Title = "...",
};
dc.Courses.Add(course);
await dc.SaveChangesAsync();
```

## 测试

| 测试文件 | 覆盖范围 |
|----------|---------|
| `tests/10E0.Core.Tests/Abstractions/MultiTenantEntityAcceptanceTests.cs` | 契约（接口签名、继承） |
| `tests/10E0.Core.Tests/Auth/HttpTenantContextAcceptanceTests.cs` | HTTP 实现（有 claim / 无 claim / 空字符串 / 未认证 / 幂等） |
| `tests/10E0.Core.Tests/Auth/Jwt/TenantIdJwtClaimAcceptanceTests.cs` | JWT 签发（透传 / 缺省 / 解析回读 / refresh 保留） |
| `tests/10E0.Core.Tests/DataContext/TenantQueryFilterAcceptanceTests.cs` | Query Filter（注册 / 跨租户隔离 / 超管 bypass / 软删除共存） |

## 设计决策

1. **Tenant 字段类型用 `string`（非 Guid）**：兼容业务编码（如 `"acme-corp"`）和 GUID。
2. **`null` 走"安全默认"**：未登录 / 无 claim → 隐藏所有租户行，符合最小权限原则。
3. **超管 bypass 用 OR 短路**：`BypassFilters || (e.TenantId == CurrentTenantId)`，
   EF Core 10 会把 `BypassFilters` 提到外层 `WHERE`，不影响普通租户的索引利用。
4. **不与 `DataPrivilege` 冲突**：`IgnoreQueryFilters("Tenant")` 只旁路租户过滤；
   `DataPrivilege:Xxx` 仍生效。`PermissionsOptions.SuperUserRoles` 控制 `BypassFilters` 短路两者。
5. **Refresh 阶段必须重读 `user.TenantId`**：用户被 admin 迁到别的租户后，
   下次 refresh 拿到的就是新租户（旧 token 上的 claim 不会持久化）。
