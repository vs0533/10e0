# AGENTS.md — 给 AI 助手 / 开发者的 10E0 使用指南

> **读者画像**:你刚 `dotnet add package TenE0.Core`,或被丢进一个用 10E0 的项目,要用 AI 帮忙写业务。
> 这份文档把你从"零认知"带到"能独立加一个完整 feature"。所有内容都与源码对齐(`TenE0.Core` .NET 10 / C# 14)。
>
> **AI 助手读这份文档时**:跳到 [§5 黄金 6 步](#5-加一个新业务的标准-6-步),照着 `src/10E0.Api` 的 Demo 模板克隆即可。
> 不要臆造 API,所有命名都对得上源码。

---

## 1. 这个框架是什么

**10E0 (TenE0)** 是一个基于 **Clean Architecture + DDD + CQRS** 的企业级后端框架(.NET 10 / C# 14)。

核心设计取舍:

- **自建 CQRS 分发器**(`ICommandDispatcher`),**不依赖 MediatR**(规避 12.x+ 商业许可风险)
- **Pipeline Behaviors** 链:`Logging → Transaction → Permission → Handler`(类 ASP.NET Core 中间件,洋葱式)
- **EF Core 10 + `IDbContextFactory`** 作用域工厂模式;`AuditInterceptor` 自动填充审计字段 + 软删除
- **Outbox Pattern** 领域事件:同事务落库 + 后台 Relay 异步发布(最终一致性)
- **声明式 DSL**:权限 / 流水号 / 导入导出列 / 定时任务 / 状态机 全部用 **C# Attribute + Fluent API** 表达,**没有外部 YAML schema**
- 多租户 / 三层权限(功能+字段+行级)/ 物化路径树 / 动态查询引擎 / 文件服务 / 工作流 / 限流验证码

**命名空间**:全部以 `TenE0.Core.*` 开头。NuGet 包名 `TenE0.Core`,程序集名 `10E0.Core`。

---

## 2. 最小可运行 Program.cs(新版:`AddTenE0All` 一键聚合)

10E0 的注册路径有两种,看场景选:

### A. 推荐:一行 `AddTenE0All`(适合新项目)

```csharp
using TenE0.Core.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTenE0All<AppDbContext>(builder.Configuration, opt =>
{
    // opt.Provider 默认按连接串自动探测,也可显式指定:
    opt.Provider = DatabaseProvider.SqlServer;   // SqlServer / PostgreSQL / SQLite / InMemory
    opt.ConnectionString = builder.Configuration.GetConnectionString("Default")!;
    opt.HandlerAssemblies = [typeof(Program).Assembly];

    // 必填:JWT SigningKey(从配置/环境变量/密钥服务读,切勿硬编码)
    opt.Identity = id =>
    {
        id.Jwt.SigningKey = builder.Configuration["Jwt:SigningKey"]
            ?? throw new InvalidOperationException("Jwt:SigningKey 未配置");
        id.Jwt.Issuer = "MyApp";
        id.Jwt.Audience = "MyApp";
        id.Permissions.SuperUserRoles.Add("super_admin");
    };

    // 基础套件(Menus/Sequences/DomainEvents/DynamicFilters/Configuration)默认已开
    // 以下是 opt-in(默认关):
    opt.Files = true;
    opt.Auditing = true;
    opt.ImportExport = true;
    opt.Realtime = true;
    opt.Workflow = true;
    opt.Scheduling = true;
    opt.Observability = true;
    // 安全三件套
    opt.RateLimiting = true;
    opt.LoginProtection = true;
    opt.Captcha = true;
});

var app = builder.Build();
app.UseExceptionHandler(_ => { });
app.UseAuthentication();
app.UseTenE0RateLimiting();   // 若启用了 opt.RateLimiting
app.UseAuthorization();
app.MapControllers();
app.Run();
```

### B. 细粒度注册(适合渐进引入、要精确控制)

把 `AddTenE0All` 展开成独立的 `AddTenE0Xxx` 调用,顺序见 [§4](#4-di-注册-按需注册速查)。

---

## 3. 实体 + DbContext(业务的根)

### 实体基类(`TenE0.Core.Entities` / `TenE0.Core.Events`)

```text
BaseEntity          // 仅 Id(string, GUID "N")
  └ TimedEntity     // + CreateTime/CreateBy/UpdateTime/UpdateBy(拦截器自动填)
       └ AuditedEntity   // + IsSoftDelete/DeleteTime/DeleteBy  ← 最常用
            └ AggregateRoot  // + 领域事件 Raise/ClearEvents + Outbox
```

- **不需要事件** → 用 `AuditedEntity`(95% 场景)
- **有业务方法要发领域事件**(订单提交、审批通过) → 用 `AggregateRoot`,在方法里调 `Raise(event)`
- 树形结构 → `TreeAuditedEntity`(+ `ParentId`)
- 多租户 → 实现 `IMultiTenantEntity`(+ `TenantId`,EF 自动加 Named Filter)

**主键统一是 `string`(GUID 字面量)**,不要改成 int。

### DbContext 基类(`TenE0.Core.DataContext`)

```csharp
using TenE0.Core.Abstractions;
using TenE0.Core.DataContext;

public class AppDbContext(
    DbContextOptions<AppDbContext> options,
    ICurrentUserContext currentUser,
    IDataAccessPolicy accessPolicy,
    IEnumerable<IEntityFilterContributor> filters,
    IDynamicFilterProvider dynamicFilterProvider)
    : TenE0SystemDbContext(options, currentUser, accessPolicy, filters, dynamicFilterProvider)
{
    public DbSet<Product> Products => Set<Product>();
}
```

- `TenE0SystemDbContext` 内置框架表(用户/角色/权限/组织/菜单/Outbox/...)
- 扩展用户字段(头像/部门/生日) → 用 `TenE0SystemDbContext<TUser, TRole>`,`TUser : TenE0User`

---

## 4. DI 注册(按需注册速查)

`TenE0.Core.DependencyInjection` 下所有 `AddTenE0Xxx` 扩展。**用 `AddTenE0All` 时这些都自动调,以下仅供手动注册参考**:

| 扩展 | 作用 | 备注 |
|---|---|---|
| `AddTenE0Core()` | 基础契约(`ICurrentUserContext` / `IErrs` / 缓存键) | 最底层,所有项目都要 |
| `AddTenE0DataContext<TContext>(string connStr, DatabaseProvider?)` | EF Core + `IDbContextFactory` + 拦截器 | provider 可省(自动探) |
| `AddTenE0Cqrs(params Assembly[])` | `ICommandDispatcher` + 扫描注册 handler | **必传业务程序集** |
| `AddTenE0EntityService()` | 通用 CRUD 服务 `IEntityService` | |
| `AddTenE0Identity<TContext>(...)` 或 `<TUser,TRole,TContext>` | JWT + 权限 + 组织,一站式 | `opt.Jwt.SigningKey` 必填 |
| `AddTenE0Permissions(...)` / `AddTenE0PermissionsFromAssembly(asm)` | 权限评估器 + 扫描 `IPermissionProvider` | |
| `AddTenE0Menus<TContext>()` | 菜单树(物化路径) | |
| `AddTenE0Sequences<TContext>()` | 流水号生成(`[Sequence]`) | |
| `AddTenE0DomainEvents<TContext>(...)` + `AddTenE0DomainEventHandlersFromAssembly(asm)` | Outbox + 领域事件订阅者 | |
| `AddTenE0DynamicFilters<TContext>()` | 运行时 JSON 数据过滤 | |
| `AddTenE0Configuration<TContext>()` | 数据字典 / 系统参数 | |
| `AddTenE0Files<TContext>(...)` | 文件上传(本地/OSS/S3) | opt-in |
| `AddTenE0Auditing<TContext>(...)` | 操作审计日志 | opt-in |
| `AddTenE0ImportExport(...)` | Excel(ClosedXML)/CSV + ImportExecutor | opt-in |
| `AddTenE0Realtime(...)` | SignalR 声明式推送 | opt-in |
| `AddTenE0Scheduling<TContext>(...)` | Cron 定时任务 + 集群锁 | opt-in |
| `AddTenE0Observability<TContext>(...)` | HealthChecks + Metrics 埋点 | opt-in;OTel SDK 仍在 app 层装配 |
| `AddTenE0ApiVersioning()` | Asp.Versioning + 每版本 OpenAPI | |
| `AddTenE0ExceptionHandler()` | 集中异常 → ApiResult 映射 | |

---

## 5. 加一个新业务的标准 6 步

这是 AI 写新 feature 时**最该遵循的模板**。范本在 `src/10E0.Api/`(DemoEntity 全套)。

### 步骤 1:定义实体

```csharp
using TenE0.Core.Events;
using TenE0.Core.Sequences;
using TenE0.Core.ImportExport.Mapping;

public class Product : AggregateRoot   // 普通表用 AuditedEntity 即可
{
    [Sequence("product", "PRD-{yyyyMMdd}-{0000}")]
    [ImportIgnore]
    [ExportColumn("编码", Order = 1)]
    public string Code { get; set; } = "";

    [ImportColumn("名称", Required = true)]
    [ExportColumn("名称", Order = 2)]
    public string Name { get; set; } = "";

    [ExportColumn("价格", Order = 3, Format = "N2")]
    public decimal Price { get; set; }

    // 业务方法封装状态变更,事件走 Raise(领域事件)
    public void Activate()
    {
        if (IsActive) return;
        IsActive = true;
        Raise(new ProductActivatedEvent(Id, Code));
    }

    public bool IsActive { get; private set; }
}
```

### 步骤 2:注册到 DbContext

```csharp
public DbSet<Product> Products => Set<Product>();
```

### 步骤 3:定义 Command / Query(声明式权限)

```csharp
using TenE0.Core.Abstractions;
using TenE0.Core.Permissions;

[RequirePermission(ProductPermissions.View)]
public sealed record ListProductsQuery : IQuery<List<ProductView>>;

[RequirePermission(ProductPermissions.Create)]
public sealed record CreateProductCommand(string Name, decimal Price) : ICommand<string>;

[RequirePermission(ProductPermissions.Update)]
public sealed record UpdateProductCommand(string Id, string Name, decimal Price) : ICommand<bool>;

[RequirePermission(ProductPermissions.Delete)]
public sealed record DeleteProductCommand(string Id) : ICommand<bool>;
```

### 步骤 4:写 Handler(走 `IEntityService` 通用 CRUD)

```csharp
using TenE0.Core.Abstractions;
using TenE0.Core.EntityService;
using TenE0.Core.EntityService.Validators;

public sealed class CreateProductHandler(
    IDbContextFactory<AppDbContext> dcFactory,
    IEntityService entitySvc)
    : ICommandHandler<CreateProductCommand, string>
{
    public async Task<string> HandleAsync(CreateProductCommand cmd, CancellationToken ct)
    {
        await using var dc = await dcFactory.CreateDbContextAsync(ct);
        var p = new Product { Name = cmd.Name, Price = cmd.Price };

        await entitySvc.CreateAsync(dc, p, new EntityWriteOptions
        {
            UniqueValidators = [Unique.Field(p, x => x.Name)],  // 唯一性校验
            BeforeSaveAsync = _ =>
            {
                p.RaiseInternal(new ProductCreatedEvent(p.Id, p.Code));
                return Task.CompletedTask;
            }
        }, ct);

        return p.Id;
    }
}
```

**关键约定**:
- Handler 通过构造函数注入依赖(不继承大基类)
- `IDbContextFactory<T>` `await using` 创建短作用域 DbContext
- 写操作走 `IEntityService.CreateAsync/UpdateAsync/DeleteAsync`,它会处理审计字段、唯一性、字段级权限、M:N、软删除
- 部分更新:把客户端提交的字段集合传 `EntityWriteOptions.PostedProperties`
- 领域事件:聚合内部业务方法用 `protected Raise(...)`;`BeforeSaveAsync` 钩子在聚合外部触发用 `RaiseInternal(...)`

> 📖 **读侧 Query Handler**:列表 / 详情 / 分页 / 统计这类读场景,用 `IEntityQueryService`(与 `IEntityService` 对称的读侧服务),自动复用 Named Query Filter(软删/行级权限/租户)、声明式筛选(字段白名单防注入)、投影到 View。范本:`src/10E0.Api/Handlers/PagedDemosQueryHandler.cs`。详见 `docs/28-entity-query-service.md`。

### 步骤 5:挂端点(Minimal API)

```csharp
app.MapPost("/products", async (CreateProductDto dto, ICommandDispatcher d, IErrs errs, CancellationToken ct) =>
{
    var id = await d.SendAsync(new CreateProductCommand(dto.Name, dto.Price), ct);
    return errs.IsValid
        ? Results.Ok(new { id })
        : Results.BadRequest(ApiResult<object>.FromErrs(errs));
});
```

**响应形状统一用 `ApiResult<T>`**(`TenE0.Core.Common`)。异常由 `TenE0ExceptionHandler` 集中映射,**端点内不要写 try/catch PermissionDeniedException**。

### 步骤 6:注册权限 key

```csharp
public static class ProductPermissions
{
    public const string View = "product.view";
    public const string Create = "product.create";
    public const string Update = "product.update";
    public const string Delete = "product.delete";
}

public sealed class ProductPermissionProvider : IPermissionProvider
{
    public IEnumerable<PermissionDefinition> Define() =>
    [
        new(ProductPermissions.View,   "查看产品", "product"),
        new(ProductPermissions.Create, "创建产品", "product"),
        // ...
    ];
}
```

`AddTenE0PermissionsFromAssembly` 会自动扫描注册 `IPermissionProvider`。再在 seeder 里给角色授权即可。

---

## 6. 声明式 DSL 速查(全部 C# Attribute / Fluent API)

10E0 的"低代码"就是这一组 attribute + fluent API。**没有外部 DSL 文件,全部在 C# 里,编译期可查**。

| 能力 | 写法 | 命名空间 | 作用对象 |
|---|---|---|---|
| 权限 | `[RequirePermission("key1","key2")]` | `TenE0.Core.Permissions` | Command / Query record |
| 流水号 | `[Sequence("key","PRD-{yyyyMMdd}-{0000}")]` | `TenE0.Core.Sequences` | 实体 string 属性 |
| 导入列 | `[ImportColumn("名称", Required=true)]` | `TenE0.Core.ImportExport.Mapping` | 实体属性 |
| 导入忽略 | `[ImportIgnore]` | 同上 | 实体属性 |
| 导出列 | `[ExportColumn("名称", Order=1, Format="N2")]` | 同上 | 实体属性 |
| 导出忽略 | `[ExportIgnore]` | 同上 | 实体属性 |
| 定时任务 | `[Scheduled("0 0 9 * * ?", Description=..., MaxRetries=3)]` | `TenE0.Core.Scheduling` | `ScheduledJobBase` 子类 |
| 状态机 | `[StateMachine]` + `StateMachineDefinitionBase<TState,TAction>` fluent | `TenE0.Core.Workflow.StateMachine` | 流程定义类 |
| 审批流 | `ProcessBuilder.Create(...).Start().Approval().End().Build()` | `TenE0.Core.Workflow.Definitions` | 流程定义类 |
| 行级过滤 | 继承 `EntityFilterContributor<TEntity>` 并注册为 `IEntityFilterContributor` | `TenE0.Core.Permissions.DataFilter` | 实体类型 |
| 多租户 | 实现 `IMultiTenantEntity`(+ `TenantId`) | `TenE0.Core.Abstractions` | 实体类型 |
| 软删除 | 继承 `AuditedEntity` 即自动 | `TenE0.Core.Entities` | 实体类型 |
| 测试跳过管道 | `[SkipBehaviorInTestEnv]` | `TenE0.Core.Cqrs` | Command record |

### 状态机 Fluent 示例

```csharp
public class OrderStateMachine : StateMachineDefinitionBase<OrderState, OrderAction>
{
    public override StateMachineDefinition<OrderState, OrderAction> Define()
        => StateMachine.Create<OrderState, OrderAction>(OrderState.Draft)
            .On(OrderAction.Submit).Transit(OrderState.Draft).To(OrderState.Submitted)
                .Guard<Order>(o => o.Items.Count > 0, "ORDER_NO_ITEMS").And()
            .On(OrderAction.Cancel).FromAny().To(OrderState.Cancelled)
                .Guard<Order>(o => o.State != OrderState.Completed, "ORDER_DONE").And()
            .Build();
}
```

### Cron 表达式约定

`[Scheduled]` 用 **6 字段含秒**的 Cron(如 `"0 0 9 * * ?"` = 每天 9:00)。这是 Quartz/Cronos 风格,末位是星期(周一=MON),不是标准 Unix 5 字段。

---

## 7. 黄金范本路径

`src/10E0.Api/` 是一份**端到端的 feature 切片范本**,克隆它最直观:

| 看什么 | 路径 |
|---|---|
| 实体 + attribute 全家桶 | `src/10E0.Api/Domain/DemoEntity.cs` |
| 权限 key 定义 + Provider | `src/10E0.Api/Domain/DemoPermissions.cs` |
| Command / Query / DTO | `src/10E0.Api/Handlers/DemoCommands.cs` |
| Handler(走 EntityService) | `src/10E0.Api/Handlers/CreateDemoCommandHandler.cs` |
| 读侧 Handler(走 EntityQueryService,分页/筛选/投影) | `src/10E0.Api/Handlers/PagedDemosQueryHandler.cs` |
| Minimal API 端点 | `src/10E0.Api/Endpoints/DemoEndpoints.cs` |
| 领域事件 + 订阅者 | `src/10E0.Api/Events/` |
| 状态机定义 | `src/10E0.Api/Handlers/OrderStateMachineDefinition.cs` |
| 审批流定义 | `src/10E0.Api/Handlers/ExpenseClaimProcess.cs` |
| 定时任务 | `src/10E0.Api/Handlers/CleanupTempFilesJob.cs` |
| DbContext + 行级过滤 | `src/10E0.Api/Domain/DemoDbContext.cs` |
| 完整 Program.cs 装配 | `src/10E0.Api/Program.cs` |

> ⚠️ 注意:`10E0.Api` 的 `DemoEntity` 等是 `internal` 的,只能作为**范本**,不能直接引用。

---

## 8. AI 写代码时的硬约束(常见坑)

1. **主键类型统一 `string`**,不要写 `int Id` / `Guid Id`
2. **实体不要写无参构造暴露可变状态**;聚合的状态字段用 `private set`,通过业务方法变更
3. **Handler 不继承大基类**,纯接口 + 构造函数注入
4. **DbContext 用 `IDbContextFactory<T>` 创建**,`await using` 包裹;不要在字段上长期持有一个 DbContext
5. **写操作走 `IEntityService`**,不要直接 `dbContext.Add` + `SaveChanges`(会绕过审计/权限/唯一性校验)
6. **权限声明贴在 Command/Query 上**(`[RequirePermission]`),不要在端点或 handler 里手写 `if (!hasPermission)`
7. **领域事件用 `Raise`**,不要在 handler 里直接 `IDomainEventDispatcher.PublishAsync`(那会绕过 Outbox 事务一致性)
8. **响应统一 `ApiResult<T>`**,异常走 `TenE0ExceptionHandler` 集中映射
9. **JWT `SigningKey` 必填**,从配置/环境变量读,切勿硬编码到源码
10. **CQRS handler 注册程序集要传对**:`AddTenE0Cqrs(typeof(Program).Assembly)`,否则 handler 不被发现

---

## 9. 扩展点(自定义框架行为)

| 需求 | 实现什么 | 在哪注册 |
|---|---|---|
| 自定义 Pipeline Behavior | `IPipelineBehavior<TCommand, TResult>` | DI 注册顺序决定执行顺序 |
| 自定义行级数据过滤 | `EntityFilterContributor<TEntity>` | DI 注册为 `IEntityFilterContributor` |
| 自定义文件存储后端 | `IFileStorage` | DI 替换实现 |
| 自定义缓存后端 | `IDistributedCache`(标准 ASP.NET Core) | 替换为 Redis 实现 |
| 自定义序列号规则 | `ISequenceGenerator` | DI 替换 |
| 自定义权限来源 | `IPermissionProvider` | `AddTenE0PermissionsFromAssembly` 自动扫 |
| 自定义领域事件处理 | `IDomainEventHandler<T>` | 自动扫 |

---

## 10. 深度文档(按主题查)

仓库 `docs/` 下有 27 篇专题文档。按任务查:

| 我要... | 看哪篇 |
|---|---|
| 理解整体架构 | `docs/01-architecture.md` |
| 5 分钟跑起来 | `docs/02-quickstart.md` |
| 查 DI 注册细节 | `docs/03-di-setup.md` |
| 写 Command / Query / Handler | `docs/04-cqrs.md` |
| 实体基类怎么选 | `docs/05-entities.md` |
| `IEntityService` 通用 CRUD 全貌 | `docs/06-entity-service.md` |
| EF Core 配置 / 拦截器 | `docs/07-data-context.md` |
| 登录 / 刷新 / 登出 | `docs/08-auth-jwt.md` |
| RBAC + 字段级 + 行级权限 | `docs/09-permissions.md` |
| Outbox + 领域事件 + Lock 选型 | `docs/10-domain-events.md` |
| 运行时数据过滤规则 | `docs/11-dynamic-filters.md` |
| 文件上传 / OSS / S3 / 图片 | `docs/12-files.md` |
| 组织架构(物化路径树) | `docs/13-organizations.md` |
| 菜单管理 | `docs/14-menus.md` |
| 流水号 | `docs/15-sequences.md` |
| 动态查询 / 分页 / 表达式 | `docs/16-dynamic-queries.md` |
| 多租户 | `docs/20-multi-tenancy.md` |
| 审批流 / 状态机 | `docs/21-workflow.md` |
| Excel/CSV 导入导出 | `docs/22-import-export.md` |
| SignalR 实时推送 | `docs/23-realtime.md` |
| API 版本化 | `docs/24-api-versioning.md` |
| 安全(限流/锁定/验证码) | `docs/25-security.md` |
| 可观测性(Health/Metrics/OTel) | `docs/26-observability.md` |
| 消息队列(RabbitMQ/Kafka) | `docs/27-messaging.md` |

> 📦 **只装了 NuGet 包没仓库?** 上述 docs 也随包发布,在包内 `docs/` 目录可查。仓库地址:`https://github.com/vs0533/10e0`。

---

**版本对齐**:本文档对齐 `TenE0.Core` 最新 dev 分支(.NET 10 / C# 14)。如有 API 不一致,以源码为准。
