# 权限系统

10E0 的权限系统基于 **Permission Key + 分布式缓存** 实现 RBAC，覆盖四级控制粒度。替代旧 E0 的 `ControllTag + AccessCode` 方案，全部声明式、无 handler 内联权限检查。

---

## 1. 四级权限模型

```
超管角色（SuperUserRoles）
  └→ RBAC 权限（Permission Key + 角色绑定）
       └→ 字段级权限（EntityWriteOptions.FieldPermissions）
       └→ 行级数据过滤（EntityFilterContributor<T>）
```

| 层级 | 控制粒度 | 实现方式 | 短路机制 |
|------|----------|----------|----------|
| 超管角色 | 全局绕过 | `PermissionsOptions.SuperUserRoles`，评估器直接返回 `true` | 命中即短路 |
| RBAC | 命令/操作级 | `[RequirePermission]` + `PermissionBehavior` 管道拦截 | 超管短路 |
| 字段级 | 实体属性级 | `EntityWriteOptions.FieldPermissions` 字典，`EntityService` 写入前校验 | 无单独短路 |
| 行级 | 数据可见性 | `EntityFilterContributor<T>` 注入 EF Named Query Filter | `BypassFilters` 短路 |

---

## 2. 定义权限：`IPermissionProvider`

业务模块实现 `IPermissionProvider`，返回本模块的全部 `PermissionDefinition`。系统启动时 `PermissionCatalog` 扫描所有实现，构建全局权限目录。

```csharp
public interface IPermissionProvider
{
    IEnumerable<PermissionDefinition> Define();
}

public sealed record PermissionDefinition(
    string Key,          // 唯一标识，约定 "{group}.{action}" 格式
    string DisplayName,  // 人类可读名称
    string? Group = null,
    string? Description = null);
```

**示例** — 定义权限常量 + 注册 Provider：

```csharp
// 1. 权限 key 常量（编译期安全、IDE 跳转、防拼写错误）
internal static class DemoPermissions
{
    public const string View         = "demo.view";
    public const string Create       = "demo.create";
    public const string Update       = "demo.update";
    public const string Delete       = "demo.delete";
    public const string ManageSalary = "demo.field.salary";  // 字段级权限
    public const string Admin        = "demo.admin";
}

// 2. Provider 实现
internal sealed class DemoPermissionProvider : IPermissionProvider
{
    public IEnumerable<PermissionDefinition> Define() =>
    [
        new(DemoPermissions.View,         "查看 Demo",          "demo"),
        new(DemoPermissions.Create,       "创建 Demo",          "demo"),
        new(DemoPermissions.Update,       "更新 Demo",          "demo"),
        new(DemoPermissions.Delete,       "删除 Demo",          "demo"),
        new(DemoPermissions.ManageSalary, "维护 Demo 薪资字段", "demo"),
        new(DemoPermissions.Admin,        "权限后台",           "system"),
    ];
}
```

---

## 3. 声明式权限属性：`[RequirePermission]`

在命令 Command 上标注，`PermissionBehavior` 管道自动拦截，Handler 不感知权限逻辑。

```csharp
// 单个 key
[RequirePermission(DemoPermissions.View)]
internal sealed record ListDemosQuery : IQuery<List<DemoView>>;

// 同一 attribute 内多个 key → OR 语义（任一满足即可）
[RequirePermission(DemoPermissions.View, DemoPermissions.Update)]
internal sealed record SomeQuery : IQuery<Result>;

// 多个 attribute → AND 语义（全部满足才行）
[RequirePermission(DemoPermissions.View)]
[RequirePermission(DemoPermissions.Admin)]
internal sealed record SensitiveCommand : ICommand<bool>;
```

### AND/OR 语义规则

| 写法 | 语义 | 说明 |
|------|------|------|
| `[RequirePermission(A)]` | 需要 A | 单一 key |
| `[RequirePermission(A, B)]` | 需要 A **OR** B | 单 attribute 内多 key = OR |
| `[RequirePermission(A)]` + `[RequirePermission(B)]` | 需要 A **AND** B | 多 attribute = AND |
| `[RequirePermission(A, B)]` + `[RequirePermission(C)]` | 需要 (A OR B) **AND** C | 混合 |

---

## 4. 异常处理

权限不足时 `PermissionBehavior` 抛出 `PermissionDeniedException`，API 层捕获后转为 403。

```csharp
// 管道内抛出
if (!ok)
    throw new PermissionDeniedException(typeof(TCommand).Name, attr.PermissionKeys);

// API 层捕获 → 403
app.MapPost("/demo", async (ICommandDispatcher d, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await d.SendAsync(new CreateDemoCommand(...), ct));
    }
    catch (PermissionDeniedException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 403);
    }
});
```

---

## 5. 字段级权限

通过 `EntityWriteOptions.FieldPermissions` 声明"字段 → 权限 key"映射，`EntityService` 在 Create/Update 流水线中内省实体属性并校验。

```csharp
// 1. 定义字段权限 key
public const string ManageSalary = "demo.field.salary";

// 2. 构建字段权限映射
internal static class DemoFieldPermissions
{
    public static readonly IReadOnlyDictionary<string, string> Map = new()
    {
        [nameof(DemoEntity.Salary)] = DemoPermissions.ManageSalary,
    };
}

// 3. Handler 中传入 EntityService
var salaryPerms = command.Salary.HasValue ? DemoFieldPermissions.Map : null;
await entitySvc.CreateAsync(dc, entity, new EntityWriteOptions
{
    FieldPermissions = salaryPerms,
}, ct);
```

**检查逻辑**：Create 场景检查全部受控字段；Update 场景根据 `PostedProperties` 收窄到"实际被修改"的字段。任一字段权限不足则收集错误到 `IErrs`，阻止保存。

---

## 6. 行级数据过滤

继承 `EntityFilterContributor<TEntity>` 并通过 `AddTenE0PermissionsFromAssembly` 注册，框架自动将过滤表达式注入 EF Core Named Query Filter。

```csharp
internal sealed class DemoOrgScopedFilter : EntityFilterContributor<DemoEntity>
{
    protected override Expression<Func<DemoEntity, bool>>? Build(BaseDataContext context)
    {
        var dc = (DemoDbContext)context;
        return entity =>
            dc.BypassFilters              // 超管短路
            || !dc.IsAuthenticated        // 未登录放行
            || entity.OrgId == null       // 无组织数据放行
            || entity.OrgId == dc.CurrentOrgId; // 按组织过滤
    }
}
```

- 同一实体多个 contributor 注册时，EF 自动 AND 组合所有过滤条件
- 需要 OR 语义请在单个表达式内写 `||`
- 表达式引用 `BaseDataContext` 的属性（如 `CurrentOrgId`），EF 运行时动态传入 SQL 参数

---

## 7. 分布式权限缓存

按角色缓存权限快照，每个角色独立缓存 key，grant/revoke 时精准失效。

```csharp
// 取角色缓存
var rolePerms = await cache.GetRolePermissionsAsync(roleCode, ct);
if (rolePerms is null)
{
    // 未命中 → 查 Store → 回写缓存
    rolePerms = await store.GetGrantedPermissionsAsync([roleCode], ct);
    await cache.SetRolePermissionsAsync(roleCode, rolePerms, ct);
}

// Grant / Revoke 后精准失效
await cache.InvalidateRoleAsync(roleCode, ct);
```

默认实现 `DistributedPermissionCache` 基于 `IDistributedCache`（开发用内存、生产可换 Redis），TTL 由 `PermissionsOptions.CacheDuration` 控制（默认 5 分钟）。全局失效通过 Version Stamp 策略：递增版本号使所有旧 key 自动过期。

---

## 8. 权限管理 API

提供完整的管理端点，底层 `IPermissionGrantService` 写入后自动失效缓存。

| 方法 | 路径 | 说明 |
|------|------|------|
| `GET` | `/admin/permissions` | 列出所有已定义的权限 |
| `GET` | `/admin/roles/{role}/permissions` | 列出角色已授予权限 |
| `POST` | `/admin/roles/{role}/permissions/{key}` | 授予权限（幂等，拒绝未注册 key） |
| `DELETE` | `/admin/roles/{role}/permissions/{key}` | 撤销权限 |

```csharp
// 授予
app.MapPost("/admin/roles/{role}/permissions/{key}",
    async (string role, string key, IPermissionGrantService svc, CancellationToken ct) =>
    {
        try
        {
            await svc.GrantAsync(role, key, ct);
            return Results.Ok(new { granted = true });
        }
        catch (InvalidOperationException ex)
        {
            // key 未在 PermissionCatalog 中注册
            return Results.BadRequest(new { error = ex.Message });
        }
    });

// 撤销
app.MapDelete("/admin/roles/{role}/permissions/{key}",
    async (string role, string key, IPermissionGrantService svc, CancellationToken ct) =>
    {
        await svc.RevokeAsync(role, key, ct);
        return Results.Ok(new { revoked = true });
    });
```

---

## 9. DI 注册

```csharp
// 分步注册（精细控制）
builder.Services.AddTenE0Core();                           // 必须先调用
builder.Services.AddTenE0Permissions(opt =>                // 权限核心 + PermissionBehavior 管道
{
    opt.SuperUserRoles.Add("super_admin");
    opt.CacheDuration = TimeSpan.FromMinutes(10);          // 可选，默认 5 分钟
});
builder.Services.AddTenE0PermissionStorage<AppDbContext>(); // EF 存储 + 管理服务
builder.Services.AddTenE0PermissionsFromAssembly(           // 扫描 IPermissionProvider
    typeof(Program).Assembly);                              //     + IEntityFilterContributor

// 一站式注册（推荐）
builder.Services.AddTenE0Identity<AppUser, AppDbContext>(opt =>
{
    opt.Jwt.SigningKey = "...";
    opt.Permissions.SuperUserRoles.Add("super_admin");
});
// AddTenE0Identity 内部调用链：JWT + AddTenE0Permissions + AddTenE0PermissionStorage + Organizations
```

| 方法 | 注册内容 |
|------|----------|
| `AddTenE0Permissions(Action?)` | `IPermissionEvaluator`、`IPermissionCache`、`PermissionCatalog`、`PermissionBehavior`、`SuperUserDataAccessPolicy` |
| `AddTenE0PermissionStorage<T>()` | `IPermissionStore` (Ef)、`IPermissionGrantService` |
| `AddTenE0PermissionsFromAssembly(Assembly)` | 扫描 `IPermissionProvider`（Singleton）、`IEntityFilterContributor`（Scoped） |

---

## 10. 种子数据：角色 + 初始权限

实现 `IDataSeeder` 并注册，启动时由 `DatabaseInitializerService` 按 `Order` 排序执行。

```csharp
public interface IDataSeeder
{
    Task SeedAsync(DbContext context, CancellationToken cancellationToken);
    int Order => 0;
}

// 注册
builder.Services.AddScoped<IDataSeeder, PermissionSeeder>();
builder.Services.AddScoped<IDataSeeder, AuthSeeder>();

// 示例 Seeder（Order=100，先于 AuthSeeder 的 Order=200 执行）
internal sealed class PermissionSeeder(IDbContextFactory<DemoDbContext> dcFactory) : IDataSeeder
{
    public int Order => 100;

    public async Task SeedAsync(DbContext context, CancellationToken cancellationToken)
    {
        await using var dc = await dcFactory.CreateDbContextAsync(cancellationToken);

        if (await dc.Roles.AnyAsync(cancellationToken)) return; // 幂等

        dc.Roles.AddRange(
            new TenE0Role { Code = "viewer",      Name = "查看者" },
            new TenE0Role { Code = "editor",      Name = "编辑者" },
            new TenE0Role { Code = "manager",     Name = "管理者" },
            new TenE0Role { Code = "super_admin", Name = "超级管理员" });

        dc.RolePermissions.AddRange(
            new() { RoleCode = "viewer",  PermissionKey = DemoPermissions.View },
            new() { RoleCode = "editor",  PermissionKey = DemoPermissions.View },
            new() { RoleCode = "editor",  PermissionKey = DemoPermissions.Create },
            new() { RoleCode = "editor",  PermissionKey = DemoPermissions.Update },
            new() { RoleCode = "manager", PermissionKey = DemoPermissions.Admin });

        await dc.SaveChangesAsync(cancellationToken);
    }
}
```

---

> **下一步**：本文档覆盖权限系统的完整设计。DI 注册参考见 [03-di-setup.md](03-di-setup.md)，行级过滤深入见 `src/10E0.Core/Permissions/DataFilter/CLAUDE.md`。
