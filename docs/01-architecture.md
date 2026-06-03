# 10E0 架构总览

## 什么是 10E0

**10E0（TenE0）** 是下一代企业低代码框架，从旧 `E0.Core` (.NET 6) 完全重构而来。目标框架 **.NET 10 / C# 14**，采用 **Clean Architecture + DDD + CQRS** 架构风格，内建权限、事件、文件、菜单、组织、序列号等企业级基础设施。

> **NuGet 包**：`TenE0.Core` | **许可证**：MIT | **仓库**：[github.com/vs0533/10e0](https://github.com/vs0533/10e0)

## 技术栈

| 层次 | 技术 |
|------|------|
| 运行时 | .NET 10 |
| 语言 | C# 14（NRT 全开，`TreatWarningsAsErrors`） |
| ORM | EF Core 10（`IDbContextFactory` 作用域工厂模式） |
| 认证 | JWT Bearer（自建 Token 服务，不依赖 IdentityServer） |
| 缓存 | `IDistributedCache`（默认内存，可替换 Redis） |
| 图片处理 | SixLabors.ImageSharp |
| 测试 | xUnit + EF Core InMemory + coverlet |

## 项目结构

```
10e0.slnx                  — .NET 10 解决方案（slnx 格式）
Directory.Build.props       — 集中构建设置（net10.0, C# 14）
.editorconfig               — 代码风格强制（EnforceCodeStyleInBuild）

src/
├── 10E0.Api/               — HTTP API 层（Minimal API，应用入口 + 演示）
└── 10E0.Core/              — 框架核心（类库，NuGet 包: TenE0.Core）

tests/
├── 10E0.Api.Tests/         — Api 集成测试（xUnit + WebApplicationFactory）
└── 10E0.Core.Tests/        — Core 单元测试（xUnit + coverlet）

.github/workflows/
├── pr-build.yml            — PR 构建 + 测试 + 覆盖率
├── claude-review.yml       — AI 自动代码审查（Qwen）
└── release.yml             — 自动发版（SemVer + GitHub Release + NuGet）
```

引用关系：`10E0.Api` → `10E0.Core`

## 分层架构

10E0 遵循 Clean Architecture 四层模型，各层依赖严格向内：

```
┌──────────────────────────────────────┐
│         API 层（Minimal API）         │  ← HTTP 端点、DTO、认证配置
├──────────────────────────────────────┤
│   CQRS 层（命令/查询 + Pipeline）      │  ← CommandDispatcher、Behaviors
├──────────────────────────────────────┤
│          领域层（Domain）             │  ← 实体、聚合根、领域事件、仓储接口
├──────────────────────────────────────┤
│      基础设施层（Infrastructure）      │  ← EF Core、文件存储、缓存、序列号
└──────────────────────────────────────┘
```

- **API 层**：`Program.cs` 定义所有 Minimal API 端点，直接使用 `ICommandDispatcher` 发送命令/查询
- **CQRS 层**：自建 `CommandDispatcher`（不依赖 MediatR），Pipeline Behaviors 链横切处理
- **领域层**：`AggregateRoot`、`IDomainEvent`、接口契约（`ICommand<T>`、`IErrs` 等）
- **基础设施层**：`TenE0SystemDbContext`、`IFileStorage`、`ISequenceGenerator` 等具体实现

## Pipeline Behavior 链

每个命令进入 `CommandDispatcher` 后，按注册顺序依次经过 Behaviors，形成洋葱式执行链：

```
请求进入
  │
  ▼
┌───────────────────────────────────────────┐
│  LoggingBehavior         (最外层)          │  ← 记录命令类型、耗时、结果
│  ┌─────────────────────────────────────┐  │
│  │ TransactionBehavior    (中层)        │  │  ← ITransactional 命令包裹事务
│  │ ┌─────────────────────────────────┐  │  │     用 Savepoint 替代嵌套事务
│  │ │ PermissionBehavior  (最内层)    │  │  │  ← 校验 [RequirePermission] 特性
│  │ │ ┌───────────────────────────┐  │  │  │
│  │ │ │   Handler（业务逻辑）      │  │  │  │
│  │ │ └───────────────────────────┘  │  │  │
│  │ └─────────────────────────────────┘  │  │
│  └─────────────────────────────────────┘  │
└───────────────────────────────────────────┘
  │
  ▼
响应返回
```

DI 注册顺序决定执行顺序：

```csharp
services.AddTransient<IPipelineBehavior<,>, LoggingBehavior<,>>();      // 1. 最先进入
services.AddTransient<IPipelineBehavior<,>, TransactionBehavior<,>>();  // 2. 正中间
services.AddTransient<IPipelineBehavior<,>, PermissionBehavior<,>>();   // 3. 最后进入 → 最靠近 Handler
```

## 20 个核心模块

| # | 模块 | 一行描述 |
|---|------|----------|
| 1 | **Abstractions** | 全局契约：`ICommand<T>`、`ICurrentUserContext`、`IErrs` 等接口定义 |
| 2 | **Auth** | 当前用户上下文实现（HTTP / Ambient 两种模式） |
| 3 | **Auth/Jwt** | JWT 认证全流程：登录、刷新、登出、Token 生成与验证 |
| 4 | **Common** | 通用工具类（`ApiResult<T>`） |
| 5 | **Cqrs** | 自建轻量 CQRS 分发器 + Pipeline Behaviors（替代 MediatR） |
| 6 | **DataContext** | EF Core DbContext 基类 + `AuditInterceptor`（自动审计字段填充） |
| 7 | **DependencyInjection** | 各模块独立 `AddTenE0*()` DI 注册扩展方法 |
| 8 | **DynamicFilters** | 动态数据过滤引擎：运行时 SQL WHERE 注入 + 管理 API |
| 9 | **Entities** | 实体基类层次：`BaseEntity` → `TimedEntity` → `AuditedEntity` → `TreeAuditedEntity` |
| 10 | **EntityService** | 通用 CRUD 服务：部分更新、唯一性校验、字段级权限、M:N 自动处理 |
| 11 | **Errors** | 请求级错误收集袋（`IErrs`） |
| 12 | **Events** | 领域事件 + Outbox Pattern：`AggregateRoot`、`OutboxInterceptor`、`OutboxRelayService` |
| 13 | **Files** | 文件上传/下载：本地/S3/OSS 存储后端 + 图片缩放/水印（新增模块） |
| 14 | **Hosting** | 启动期数据库初始化和种子数据执行 |
| 15 | **Json** | JSON 序列化工具：`PostedProperties` 解析、`HttpRequest.Body` 重置 |
| 16 | **Menus** | 菜单树 CRUD + 按角色分配（支持无限层级） |
| 17 | **Organizations** | 组织架构树管理（物化路径）：增删改移、子树/祖先查询 |
| 18 | **Permissions** | 权限评估 + 内存缓存 + 程序集扫描 + `PermissionBehavior` + 行级过滤 |
| 19 | **Queries** | 动态查询扩展：`DynamicWhere`、`DynamicOrderBy`、分页 |
| 20 | **Sequences** | 流水号生成器：日期重置、前缀模板（如 `DEMO-{yyyyMMdd}-{0000}`） |

## 标准 DI 注册顺序

以下是 `Program.cs` 推荐的完整注册链，从核心到可选模块依次添加：

```csharp
var builder = WebApplication.CreateBuilder(args);

// 1️⃣  核心基础设施（HttpContext、缓存、用户上下文、审计）
builder.Services.AddTenE0Core();

// 2️⃣  通用 CRUD 服务
builder.Services.AddTenE0EntityService();

// 3️⃣  数据库上下文（工厂模式 + 拦截器自动注入）
builder.Services.AddTenE0DataContext<DemoDbContext>((sp, options) =>
    options.UseInMemoryDatabase("10E0-demo-perm"));

// 4️⃣  CQRS 分发器 + Pipeline Behaviors + 程序集扫描 Handler
builder.Services.AddTenE0Cqrs(typeof(Program).Assembly);
builder.Services.AddTenE0TransactionBehavior<DemoDbContext>();  // 可选：启用事务管道行为

// 5️⃣  权限系统（评估器、缓存、目录扫描、PermissionBehavior）
builder.Services.AddTenE0PermissionsFromAssembly(typeof(Program).Assembly);

// 6️⃣  菜单管理
builder.Services.AddTenE0Menus<DemoDbContext>();

// 7️⃣  身份认证（等价于 JWT + 权限 + 组织的一站式注册）
builder.Services.AddTenE0Identity<AppUser, DemoDbContext>(opt =>
{
    opt.Jwt.Issuer = "10E0.Api";
    opt.Jwt.SigningKey = builder.Configuration["Jwt:SigningKey"];
    opt.Jwt.AccessTokenLifetime = TimeSpan.FromMinutes(30);
    opt.Permissions.SuperUserRoles.Add("super_admin");
});

// 8️⃣  流水号 + 领域事件 + Outbox Relay
builder.Services.AddTenE0Sequences<DemoDbContext>();
builder.Services.AddTenE0DomainEvents<DemoDbContext>(opt =>
{
    opt.BatchSize = 50;
    opt.PollInterval = TimeSpan.FromMilliseconds(500);
});

// 9️⃣  动态数据过滤
builder.Services.AddTenE0DynamicFilters<DemoDbContext>();

// 🔟  文件上传（本地存储、S3、OSS 可切换；PR #6 起 AddTenE0Files 需带 TContext 泛型）
builder.Services.AddTenE0Files<DemoDbContext>(options =>
{
    options.BasePath = "uploads";
    options.BaseUrl = "/uploads";
});
```

> **关键设计**：每个扩展方法独立注册一个模块，可按需裁剪。对比旧 `AddE0Context()` 一次性注册所有东西，新方式更灵活、更可测试。

## 相比旧 E0.Core 的关键改进

| 改进点 | 旧 E0.Core (.NET 6) | 新 10E0 (.NET 10) |
|--------|---------------------|--------------------|
| **CQRS 分发器** | 依赖 MediatR（12.x+ 商业许可风险） | 自建 `CommandDispatcher`，Wrapper 模式 + 静态缓存 |
| **DI 注册** | `AddE0Context()` 大杂烩，全部强制注册 | 每个模块独立 `AddTenE0*()`，按需组合 |
| **E0Context** | 全局 ServiceLocator 大杂烩 | 拆为独立 DI 服务，可组合、可单独 Mock |
| **嵌套事务** | `CommandManager` 嵌套事务有 Bug（内层回滚破坏外层） | `TransactionBehavior` 用 Savepoint 替代嵌套事务 |
| **M:N 关系** | 需要 `MultipleEntity` 标记基类 | EF Core Skip Navigation 自省，零标记 |
| **反射缓存** | `MetaContext` 手工反射缓存 | 改用 EF Core `IModel` 元数据 |
| **权限模型** | `ControllTag` + `AccessCode` 字符串匹配 | `Permission Key` + `[RequirePermission]` 特性 + 内存缓存 |
| **文件服务** | ❌ 不存在 | ✅ `IFileService` + 本地/S3/OSS 多后端 + 图片处理 |

## Outbox Pattern

领域事件不直接发布，而是与业务数据同事务写入 `OutboxMessage` 表，由后台 `OutboxRelayService` 异步轮询发布：

```
聚合根.Raise(event) → OutboxInterceptor 拦截 SaveChanges
    → event 写入 OutboxMessage（同事务）
    → OutboxRelayService 轮询未发送消息
    → 反射调用 IDomainEventHandler<T>.HandleAsync()
    → 标记 SentTime，保留 AttemptCount / LastError
```

这保证了事件发布的 **At-Least-Once** 语义和 **事务一致性**。

## 命名查询过滤器

EF Core 的 Global Query Filters 被自动注入到所有实现标记接口的实体上：

| 标记接口 | 注入的过滤器 |
|----------|-------------|
| `ISoftDeleteEntity` | `IsSoftDelete == false` |
| `IMultiTenantEntity` | `TenantId == currentTenantId` |
| `IEntityFilterContributor<T>` | 自定义行级过滤（如按组织隔离） |

业务代码无需手动加 `.Where(...)`，框架自动透明过滤。

## 框架实体

所有以 `TenE0` 为前缀的实体是框架自有表，业务项目不应直接修改其结构：

`TenE0User` · `TenE0Role` · `TenE0UserRole` · `TenE0RefreshToken` · `TenE0RolePermission` · `TenE0Org` · `TenE0Sequence` · `TenE0Menu` · `TenE0RoleMenu` · `TenE0DataFilterRule` · `TenE0FileAttachment` · `OutboxMessage`
