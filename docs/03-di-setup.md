# DI 注册参考

所有 `AddTenE0*()` 扩展方法集中在 `TenE0.Core.DependencyInjection` 命名空间下，基于 `IServiceCollection` 扩展。

---

## 0. 一键聚合注册：`AddTenE0All`（issue #160）

如果不想手写 15+ 行 `AddTenE0Xxx` 样板，用 `AddTenE0All<TContext>` 一行注册所有官方模块：

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTenE0All<AppDbContext>(builder.Configuration, opt =>
{
    opt.Provider = DatabaseProvider.PostgreSQL;          // 显式指定，避免连接串探测
    opt.ConnectionString = builder.Configuration.GetConnectionString("Default")!;
    opt.HandlerAssemblies = [typeof(Program).Assembly];

    opt.Identity = identity =>                            // 必填：JWT SigningKey
    {
        identity.Jwt.SigningKey = builder.Configuration["Jwt:SigningKey"]
            ?? throw new InvalidOperationException("Jwt:SigningKey 未配置");
        identity.Jwt.AccessTokenLifetime = TimeSpan.FromMinutes(30);
        identity.Permissions.SuperUserRoles.Add("super_admin");
    };

    // 按需启用（默认关闭）
    opt.Auditing = true;        // #152
    opt.Files = true;           // 本地存储
    opt.ImportExport = true;    // #154
    opt.Realtime = true;        // #155
    opt.Workflow = true;        // #156 epic
});

// 业务模块（seeder / 自定义服务）走 IAppModule（见第 6 节）
builder.Services.AddAppModule<MyAppModule>(builder.Configuration);

var app = builder.Build();
app.Run();
```

**默认启用**（几乎所有项目都要）：Core / EntityService / DataContext / Cqrs / Permissions /
Identity / Menus / Sequences / DomainEvents / DynamicFilters / Configuration。

**默认关闭**（按需 opt-in，避免引入不必要依赖）：Files / Auditing / ImportExport / Realtime / Workflow。

扩展用户字段用泛型重载 `AddTenE0All<AppUser, AppDbContext>(...)`。

### Database provider 装配（`IDbProviderConfigurator`）

Core 不直接引用 SqlServer / Npgsql / Sqlite 包（避免框架膨胀，与 Microsoft `AddDbContext` 设计一致）。
provider 的 `UseSqlServer` / `UseNpgsql` / `UseSqlite` 扩展方法在 **app 层** 调用 —— 通过 SPI 注册：

```csharp
// app 层：定义你的 provider 装配器
public sealed class NpgsqlConfigurator : IDbProviderConfigurator
{
    public DatabaseProvider Provider => DatabaseProvider.PostgreSQL;
    public void Configure(IServiceProvider services, DbContextOptionsBuilder options, string connectionString)
        => options.UseNpgsql(connectionString);
}

// app 启动时注册（AddTenE0All 之前）
builder.Services.AddTenE0DbProviderConfigurator(new NpgsqlConfigurator());
```

`AddTenE0All` 内部用 `AddTenE0DataContext<TContext>(connectionString, provider)` 连接串重载装配，
按 `TenE0Options.Provider` 或连接串探测结果（`ConnectionStringProbe`）匹配装配器。

**连接串探测规则**（`provider` 为 `null` 时）：

| 连接串特征 | 探测结果 |
|-----------|---------|
| `Server=` / `Data Source=` + 多段（SQL Server ADO.NET） | `SqlServer` |
| `Host=` + `Port=5432`，或 `UserID=`（Npgsql 风格） | `PostgreSQL` |
| `Data Source=` + `.db` / `.sqlite` / `:memory:` | `SQLite` |
| 均不命中 | 抛 `InvalidOperationException`，提示显式传 `Provider` |

> 探测失败时异常消息只暴露连接串前缀（脱敏），不含密码等敏感值。

`AddTenE0DataContext` 同时提供三个重载，可单独使用（不依赖 `AddTenE0All`）：

```csharp
// 1. 高级场景（保留，自定义 optionsAction）
AddTenE0DataContext<TContext>(Action<IServiceProvider, DbContextOptionsBuilder>)

// 2. 连接串 + provider（新）
AddTenE0DataContext<TContext>(string connectionString, DatabaseProvider? provider = null)
AddTenE0DataContext<TContext>(string connectionString, DatabaseProvider? provider, Action<DbContextOptionsBuilder>? extraConfigure)

// 3. 从 IConfiguration 读连接串（新）
AddTenE0DataContext<TContext>(IConfiguration configuration, string name = "Default", DatabaseProvider? provider = null)
```

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
  → AddTenE0Files<TContext>() / AddTenE0FilesWithAliyunOss<TContext>() / AddTenE0FilesWithAwsS3<TContext>()
```

`AddTenE0Core()` 必须最先调用，它提供所有下游组件依赖的基础服务。
（用 `AddTenE0All` 时上述顺序由聚合方法内部保证，调用方无需关心。）

---

## 2. 完整方法参考表

| 方法 | 注册的服务 | 说明 |
|------|-----------|------|
| `AddTenE0All<TContext>(IConfiguration, Action<TenE0Options>?)` | 全部官方模块（默认 + 按需 opt-in） | **一键聚合**（issue #160），默认 `TenE0User` |
| `AddTenE0All<TUser,TContext>(IConfiguration, Action<TenE0Options>?)` | 同上 | 自定义 User 类型 |
| `AddTenE0Core()` | `HttpContextAccessor`, `TimeProvider`, `DistributedMemoryCache`, `ICurrentUserContext`, `IErrs`, `IDataAccessPolicy`, `AuditInterceptor` | **必须最先调用** |
| `UseTenE0AmbientCurrentUser()` | 切换 `ICurrentUserContext` 为 `AsyncLocal` 实现 | 后台任务 / 测试场景 |
| `AddTenE0DataContext<T>(Action<IServiceProvider, DbContextOptionsBuilder>)` | `IDbContextFactory<T>`, `DatabaseInitializerService<T>` | DbContext 工厂注册（高级） |
| `AddTenE0DataContext<T>(string, DatabaseProvider?)` | 同上 | 连接串 + provider（issue #160 简化重载） |
| `AddTenE0DataContext<T>(IConfiguration, string, DatabaseProvider?)` | 同上 | 从配置读连接串（issue #160） |
| `AddTenE0DbProviderConfigurator(IDbProviderConfigurator)` | `IDbProviderConfigurator` | provider 装配 SPI（app 层注册，见第 0 节） |
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
| `AddTenE0Files<TContext>(Action?)` | `IFileService`, `IFileStorage`(Local), `IImageProcessor` | 文件上传（本地） |
| `AddTenE0FilesWithAliyunOss<TContext>(Action)` | 同上 + `AliyunOssStorage` | 文件上传（OSS） |
| `AddTenE0FilesWithAwsS3<TContext>(Action)` | 同上 + `AwsS3Storage` | 文件上传（S3） |

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
builder.Services.AddTenE0FilesWithAliyunOss<AppDbContext>(opt =>
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

## 5. `TenE0Options` 完整字段（`AddTenE0All` 用）

| 字段 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `ConnectionString` | `string?` | `null`（读 `ConnectionStrings:Default`） | 数据库连接串 |
| `Provider` | `DatabaseProvider?` | `null`（自动探测） | 显式指定 provider，覆盖探测 |
| `HandlerAssemblies` | `Assembly[]?` | 入口程序集 | CQRS / 权限 / 领域事件处理器扫描 |
| `Identity` | `Action<TenE0IdentityOptions>?` | **必填** | JWT + Permissions + Organizations |
| `Menus` | `bool` | `true` | 菜单服务 |
| `Sequences` | `bool` | `true` | 流水号生成器 |
| `DomainEvents` | `bool` | `true` | 领域事件 + Outbox |
| `DomainEventsOptions` | `Action<OutboxRelayOptions>?` | `null` | Outbox relay 配置 |
| `DynamicFilters` | `bool` | `true` | 动态数据过滤 |
| `Configuration` | `bool` | `true` | 数据字典 + 系统参数 |
| `Files` | `bool` | `false` | 文件上传（本地存储） |
| `FilesOptions` | `Action<LocalStorageOptions>?` | `null` | 文件存储配置 |
| `Auditing` | `bool` | `false` | 审计日志（#152） |
| `AuditingOptions` | `Action<AuditOptions>?` | `null` | 审计配置 |
| `ImportExport` | `bool` | `false` | 导入导出（#154） |
| `Realtime` | `bool` | `false` | 实时推送 SignalR（#155，自动接入 hub token query） |
| `Workflow` | `bool` | `false` | 工作流：状态机 + 流程定义 + 运行时（#156 epic） |
| `WorkflowAssemblies` | `Assembly[]?` | 同 `HandlerAssemblies` | 工作流状态机扫描 |

---

## 6. 业务模块装配：`IAppModule`

`AddTenE0All` 注册**框架**；业务模块（demo / 租户模块 / 订单模块等）走 `IAppModule`
注册**业务** —— 两者协同，互不互斥。

```csharp
public sealed class OrderModule : IAppModule
{
    public int Order => 100;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<IDataSeeder, OrderSeeder>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/orders", ...);
    }
}
```

挂载：

```csharp
builder.Services.AddTenE0All<AppDbContext>(builder.Configuration, opt => { ... });
builder.Services.AddAppModule<OrderModule>(builder.Configuration);

var app = builder.Build();
app.MapAppModules();   // 按 IAppModule.Order 升序挂载各模块端点
```

`Order` 控制注册顺序（数字小的先 `ConfigureServices` / `MapEndpoints`），框架壳占 `0`，业务模块用 `100` / `200` / `300`。

完整示例参见 demo 项目 `src/10E0.Api/Modules/DemoAppModule.cs`。

---

> 完整 API 文档和内部实现细节请参阅各模块下的 `CLAUDE.md`。
