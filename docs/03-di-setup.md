# DI 注册参考

所有 `AddTenE0*()` 扩展方法集中在 `TenE0.Core.DependencyInjection` 命名空间下，基于 `IServiceCollection` 扩展。

---

## 1. 注册顺序

扩展方法之间存在隐式依赖关系，推荐按以下顺序调用：

```
AddTenE0Core()
  → AddTenE0EntityService()
  → AddTenE0DataContext<TContext>()
  → AddTenE0Cqrs()
  → AddTenE0TransactionBehavior<TContext>()   // 可选
  → AddTenE0Identity<>()                       // 或手动拆分为三步
      ├─ AddTenE0JwtAuth()
      ├─ AddTenE0Permissions()
      └─ AddTenE0Organizations()
  → AddTenE0PermissionsFromAssembly()
  → AddTenE0Menus<TContext>()
  → AddTenE0Sequences<TContext>()
  → AddTenE0DomainEvents<TContext>()
  → AddTenE0DomainEventHandlersFromAssembly()
  → AddTenE0DynamicFilters<TContext>()
  → AddTenE0Files() / AddTenE0FilesWithAliyunOss() / AddTenE0FilesWithAwsS3()
```

`AddTenE0Core()` 必须最先调用，它提供所有下游组件依赖的基础服务。

---

## 2. 完整方法参考表

| 方法 | 注册的服务 | 说明 |
|------|-----------|------|
| `AddTenE0Core()` | `HttpContextAccessor`, `TimeProvider`, `DistributedMemoryCache`, `ICurrentUserContext`, `IErrs`, `IDataAccessPolicy`, `AuditInterceptor` | **必须最先调用** |
| `UseTenE0AmbientCurrentUser()` | 切换 `ICurrentUserContext` 为 `AsyncLocal` 实现 | 后台任务 / 测试场景 |
| `AddTenE0DataContext<T>(Action<IServiceProvider, DbContextOptionsBuilder>)` | `IDbContextFactory<T>`, `DatabaseInitializerService<T>` | DbContext 工厂注册 |
| `AddTenE0Cqrs(params Assembly[])` | `ICommandDispatcher`, `LoggingBehavior`, 扫描 `ICommandHandler` | CQRS 分发器 |
| `AddTenE0TransactionBehavior<T>()` | `TransactionBehavior`（Savepoint 机制） | 事务包裹 |
| `AddTenE0Permissions(Action?)` | `IPermissionEvaluator`, `IPermissionCache`, `PermissionCatalog`, `PermissionBehavior` | 权限评估 |
| `AddTenE0PermissionStorage<T>()` | `IPermissionStore`, `IPermissionGrantService` | 权限存储 |
| `AddTenE0PermissionsFromAssembly(Assembly)` | 扫描 `IPermissionProvider`, `IEntityFilterContributor` | 权限定义扫描 |
| `AddTenE0Identity<TContext>(Action)` | JWT + Permissions + Organizations（一站式） | 使用默认 `TenE0User`/`TenE0Role` |
| `AddTenE0Identity<TUser,TContext>(Action)` | 同上 | 自定义 User，默认 Role |
| `AddTenE0Identity<TUser,TRole,TContext>(Action)` | 同上 | 自定义 User + Role |
| `AddTenE0JwtAuth<TUser,TContext>(Action)` | `IPasswordHasher`, `IJwtTokenService`, 3 个命令处理器, JWT Bearer | JWT 认证 |
| `AddTenE0EntityService()` | `IEntityService` | 通用 CRUD |
| `AddTenE0Menus<T>()` | `IMenuService` | 菜单管理 |
| `AddTenE0Organizations<T>()` | `IOrgTreeService` | 组织架构 |
| `AddTenE0Sequences<T>()` | `ISequenceGenerator` | 流水号 |
| `AddTenE0DomainEvents<T>(Action?)` | `IDomainEventDispatcher`, `OutboxInterceptor`, `IOutboxPublisher`, `OutboxRelayService` | 领域事件 + Outbox |
| `AddTenE0DomainEventHandlersFromAssembly(Assembly)` | 扫描 `IDomainEventHandler<T>` | 事件处理器 |
| `AddTenE0DynamicFilters<T>()` | `IDynamicFilterProvider`, `IDataFilterRuleService` | 动态数据过滤 |
| `AddTenE0Files(Action?)` | `IFileService`, `IFileStorage`(Local), `IImageProcessor` | 文件上传（本地） |
| `AddTenE0FilesWithAliyunOss(Action)` | 同上 + `AliyunOssStorage` | 文件上传（OSS） |
| `AddTenE0FilesWithAwsS3(Action)` | 同上 + `AwsS3Storage` | 文件上传（S3） |

---

## 3. 配置选项详解

### JwtOptions

通过 `AddTenE0Identity` 或 `AddTenE0JwtAuth` 的配置委托传入：

```csharp
opt.Jwt.Issuer = "MyApp";
opt.Jwt.Audience = "MyApp";
opt.Jwt.SigningKey = "your-256-bit-secret";
opt.Jwt.AccessTokenLifetime = TimeSpan.FromMinutes(30);
opt.Jwt.RefreshTokenLifetime = TimeSpan.FromDays(14);
```

`SigningKey` 用于 HS256 签名，长度至少 32 字节。生产环境应从密钥管理服务获取。

### PermissionsOptions

```csharp
opt.Permissions.SuperUserRoles.Add("super_admin");
opt.Permissions.SuperUserRoles.Add("system_admin");
```

超管角色自动绕过权限检查，无需显式授权。

### OutboxRelayOptions

```csharp
opt.BatchSize = 50;
opt.PollInterval = TimeSpan.FromSeconds(2);
opt.MaxAttempts = 8;
```

`BatchSize` 控制每次轮询取出的待发事件数，`MaxAttempts` 达到后标记为永久失败。

### LocalStorageOptions (文件存储)

```csharp
opt.BasePath = "uploads";
opt.BaseUrl = "/uploads";
```

文件系统存储的根目录和访问路径前缀。OSS / S3 模式不需要此项。

---

## 4. 典型用法示例

一个完整的最小 API 入口通常如下：

```csharp
var builder = WebApplication.CreateBuilder(args);

// 基础服务（必须最先调用）
builder.Services.AddTenE0Core();

// 数据层 — Action<IServiceProvider, DbContextOptionsBuilder> 签名
builder.Services.AddTenE0DataContext<AppDbContext>((sp, opt) =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// CQRS + 事务
builder.Services.AddTenE0Cqrs(typeof(Program).Assembly);
builder.Services.AddTenE0TransactionBehavior<AppDbContext>();

// 身份 + 权限一站式注册
builder.Services.AddTenE0Identity<User, Role, AppDbContext>(opt =>
{
    opt.Jwt.SigningKey = builder.Configuration["Jwt:SigningKey"]
        ?? throw new InvalidOperationException("Jwt:SigningKey 未配置");
    opt.Jwt.AccessTokenLifetime = TimeSpan.FromHours(1);
    opt.Permissions.SuperUserRoles.Add("super_admin");
});
builder.Services.AddTenE0PermissionsFromAssembly(typeof(Program).Assembly);
builder.Services.AddTenE0PermissionStorage<AppDbContext>();

// 业务服务
builder.Services.AddTenE0EntityService();
builder.Services.AddTenE0Menus<AppDbContext>();
builder.Services.AddTenE0Sequences<AppDbContext>();

// 领域事件
builder.Services.AddTenE0DomainEvents<AppDbContext>();
builder.Services.AddTenE0DomainEventHandlersFromAssembly(typeof(Program).Assembly);

// 文件存储
builder.Services.AddTenE0FilesWithAliyunOss(opt =>
{
    opt.Endpoint = "oss-cn-hangzhou.aliyuncs.com";
    opt.Bucket = "my-app-bucket";
});

// 动态过滤
builder.Services.AddTenE0DynamicFilters<AppDbContext>();

var app = builder.Build();
app.Run();
```

---

> 完整 API 文档和内部实现细节请参阅各模块下的 `CLAUDE.md`。
