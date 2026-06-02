# DataContext — EF Core 数据上下文

TenE0 的 EF Core 配置以 `BaseDataContext` 和 `TenE0SystemDbContext` 两级抽象基类为核心，自动完成命名查询过滤器注册、审计字段填充、软删除转换和启动期数据初始化。

---

## 1. DbContext 继承链

```
DbContext  (EF Core 原生)
  └── BaseDataContext              ← 运行时属性 + 查询过滤器注册
        └── TenE0SystemDbContext   ← 12 个框架表 DbSet + 表映射
              └── TenE0SystemDbContext<TUser, TRole>  ← 支持扩展用户/角色类型
                    └── AppDbContext   ← 你的业务 DbContext
```

**`BaseDataContext`** 通过构造函数注入 `ICurrentUserContext`、`IDataAccessPolicy`、`IEntityFilterContributor` 和 `IDynamicFilterProvider`，利用 EF Core 10 对 `DbContext` 构造函数的 DI 支持，无需 ServiceLocator。

---

## 2. BaseDataContext 运行时属性

这些属性在每次查询时由 EF Core 参数化，过滤表达式可以安全引用：

```csharp
public string? CurrentUserCode    // 当前用户编码。未登录返回 null
public string[] CurrentRoleIds    // 当前角色 ID 列表。未登录返回空数组（非 null，便于 EF Contains 翻译）
public string[] CurrentOrgIds     // 当前组织 ID 列表。默认空数组
public bool IsAuthenticated       // 是否已认证
public bool BypassFilters         // 是否绕过所有行级过滤器（超管场景）
```

`BypassFilters` 由 `IDataAccessPolicy` 决定，`CurrentRoleIds` 在构造函数中从 `ICurrentUserContext` 获取并固化（同一请求内不变）。

---

## 3. 定义 AppDbContext

### 用法 A — 不扩展用户/角色（直接使用框架默认类型）

```csharp
public class AppDbContext(
    DbContextOptions<AppDbContext> options,
    ICurrentUserContext currentUser,
    IDataAccessPolicy accessPolicy,
    IEnumerable<IEntityFilterContributor> filters,
    IDynamicFilterProvider dynamicFilterProvider)
    : TenE0SystemDbContext(options, currentUser, accessPolicy, filters, dynamicFilterProvider)
{
    public DbSet<Course> Courses => Set<Course>();
}
```

### 用法 B — 扩展用户/角色字段

```csharp
public class AppUser : TenE0User { public string? Avatar; }
public class AppRole : TenE0Role { public string? Color; }

public class AppDbContext(
    DbContextOptions<AppDbContext> options, ...)
    : TenE0SystemDbContext<AppUser, AppRole>(options, ...)
{
    public DbSet<Course> Courses => Set<Course>();
}
```

`TenE0SystemDbContext` 内部自动调用 `ConfigureTenE0AuthTables<TUser>()` 等映射方法，业务子类只需处理自己的实体。

---

## 4. 12 个自动注册的框架 DbSet

继承 `TenE0SystemDbContext` 后，以下 DbSet 自动可用：

| DbSet | 说明 |
|-------|------|
| `Users` | 用户（可用泛型扩展） |
| `Roles` | 角色（可用泛型扩展） |
| `UserRoles` | 用户-角色绑定 |
| `RefreshTokens` | JWT 刷新令牌 |
| `RolePermissions` | 角色-权限绑定 |
| `Orgs` | 组织架构树 |
| `Sequences` | 流水号生成器 |
| `OutboxMessages` | 领域事件 Outbox |
| `Menus` | 菜单 |
| `RoleMenus` | 角色-菜单绑定 |
| `DataFilterRules` | 动态数据过滤规则 |
| `FileAttachments` | 文件附件 |

---

## 5. 三层命名查询过滤器

在 `BaseDataContext.OnModelCreating` 中按顺序自动注册，同实体的多个过滤器 **AND 组合**：

### 第一层：SoftDelete

实体实现 `ISoftDeleteEntity` 时自动注册，条件 `e.IsSoftDelete == false`。需要查询已删除数据时：

```csharp
dc.Set<DemoEntity>().IgnoreQueryFilters(["SoftDelete"]).Where(...)
```

### 第二层：DataPrivilege（行级权限）

每个 `IEntityFilterContributor` 注册一个命名过滤器 `DataPrivilege:{TypeName}`。实现示例：

```csharp
public class DemoOrgScopedFilter : EntityFilterContributor<DemoEntity>
{
    protected override Expression<Func<DemoEntity, bool>>? Build(BaseDataContext context)
    {
        return entity =>
            context.BypassFilters
            || !context.IsAuthenticated
            || entity.OrgId == null
            || context.CurrentOrgIds.Contains(entity.OrgId ?? "");  // CurrentOrgIds 是 BaseDataContext 框架属性（string[]）
    }
}
```

表达式可引用 `BaseDataContext` 的运行时属性，EF Core 自动参数化，无需重建模型。多个 contributor 对同一实体 AND 合并。

### 第三层：DynamicFilter（动态规则）

`IDynamicFilterProvider.LoadRulesAsync` 从数据库 `TenE0DataFilterRule` 表加载管理员配置的运行时规则，在 `OnModelCreating` 时注册为命名查询过滤器。规则变更后需重新加载。

---

## 6. AuditInterceptor — 审计字段 + 软删除转换

`AuditInterceptor : SaveChangesInterceptor`，在 `SaveChanges` 时自动处理：

| 场景 | 行为 |
|------|------|
| 实体 `Added` 且实现 `ITimerEntity` | 填充 `CreateTime`、`CreateBy` |
| 实体 `Modified` 且实现 `ITimerEntity` | 填充 `UpdateTime`、`UpdateBy` |
| 实体 `Deleted` 且实现 `ISoftDeleteEntity` | **转为 `Modified`**：设置 `IsSoftDelete = true`、`DeleteTime`、`DeleteBy` |
| 实体 `Deleted` 但未实现 `ISoftDeleteEntity` | 物理删除（正常 EF 行为） |

审计字段由 `AuditInterceptor` 独占控制，`EntityService` 在任何写操作中都不会修改 `Id`、`CreateTime`、`CreateBy`、`UpdateTime`、`UpdateBy`、`IsSoftDelete`、`DeleteTime`、`DeleteBy` 八个字段（硬锁定保护）。时间来源使用 `TimeProvider` 而非 `DateTime.UtcNow`，测试可替换为 `FakeTimeProvider`。

---

## 7. IDbContextFactory — Scoped 工厂模式

注册方式：

```csharp
builder.Services.AddTenE0DataContext<AppDbContext>((sp, options) =>
    options.UseSqlServer(connStr));
```

使用 **Scoped** 而非 Singleton 的原因：

- `AuditInterceptor` 依赖 `ICurrentUserContext`（Scoped），DbContext 工厂必须同生命周期才能注入正确的用户上下文
- 工厂的 `CreateDbContext()` 行为不变，仍是每次调用创建新 DbContext 实例
- 每个请求作用域拥有独立工厂实例，Scoped 拦截器随请求正确注入

多数据源场景可结合 EF Core Keyed Services 注册多个工厂。

---

## 8. DatabaseInitializerService — 启动期初始化

`DatabaseInitializerService<TContext>` 实现 `IHostedLifecycleService.StartingAsync`，在 Kestrel 开始监听端口前完成数据库初始化：

```csharp
// 在 AddTenE0DataContext 中自动注册
services.AddHostedService<DatabaseInitializerService<TContext>>();
```

流程：
1. 创建 Scoped 作用域，解析 `IDbContextFactory` 和所有 `IDataSeeder`
2. 调用 `EnsureCreatedAsync`（开发环境）或 `MigrateAsync`（生产环境）
3. 按 `seeder.Order` 升序执行所有种子数据填充
4. 最终 `SaveChangesAsync`

---

## 9. 实现 IDataSeeder

```csharp
public interface IDataSeeder
{
    Task SeedAsync(DbContext context, CancellationToken cancellationToken);
    int Order => 0;  // 数字小的先执行
}
```

使用 `AddTenE0DataContext` 后注册实现即可：

```csharp
services.AddScoped<IDataSeeder, PermissionSeeder>();  // Order=100
services.AddScoped<IDataSeeder, AuthSeeder>();         // Order=200, 等角色建好后再建用户
```

`DatabaseInitializerService` 按 `Order` 值升序依次调用，支持幂等（重复运行不会插入重复数据）。

> 💡 **为什么 Seeder 示例丢弃了 `context` 参数？**  
> 传入的 `context` 是 `DatabaseInitializerService` 创建的共享 DbContext，已附加 `AuditInterceptor` 和 `SoftDelete` 过滤器。Seeder 通常需要**绕过查询过滤器**检查已有数据（如 `Roles.AnyAsync()`），或需要独立事务控制——因此推荐注入 `IDbContextFactory` 自建 DbContext 实例。如果 Seeder 不需要绕过过滤器，可直接使用传入的 `context` 参数。：

```csharp
public class PermissionSeeder(IDbContextFactory<AppDbContext> f) : IDataSeeder
{
    public int Order => 100;

    public async Task SeedAsync(DbContext _, CancellationToken ct)  // ← 丢弃共享 context，自建独立实例
    {
        await using var dc = await f.CreateDbContextAsync(ct);
        if (await dc.Roles.AnyAsync(ct)) return; // 幂等

        dc.Roles.Add(new TenE0Role { Code = "admin", Name = "管理员" });
        await dc.SaveChangesAsync(ct);
    }
}
```
