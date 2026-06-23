# 10E0.Core — 框架核心库

命名空间 `TenE0.Core.*`，所有框架功能的承载体。被 10E0.Api 引用。

## 模块一览

| 模块目录 | 职责 | 核心接口/类 |
|----------|------|-------------|
| `Abstractions/` | 全局契约：命令、实体标记、用户上下文、错误收集、租户、token/缓存 key 抽象 | `ICommand<T>`, `IQuery<T>`, `ICommandHandler<,>`, `ICommandDispatcher`, `IPipelineBehavior`, `IBaseEntity`, `IMultiTenantEntity`, `ITenantContext`, `ICurrentUserContext`, `ITokenClaimNames` (PR #71), `ICacheKeyNamespace` (PR #71), `ErrorCodes` (PR #71), `IErrs`, `IDataAccessPolicy`, `JwtClaims` |
| `Auth/` | 当前用户上下文实现（HTTP / Ambient）+ 租户上下文 | `HttpCurrentUserContext`, `AmbientCurrentUserContext`, `HttpTenantContext` |
| `Auth/Jwt/` | JWT 认证全流程（Auth 的子目录） | `JwtTokenService`, `LoginCommandHandler` |
| `Caching/` | 多级缓存抽象 + 原子计数器 + TTL 选项 + 扩展（PR #64 + PR #88） | `IMultiLevelCache` (含 `GetAsync` 纯读 API), `IAtomicCounter`, `CacheOptions`, `CacheEntryOptionsExtensions`；默认实现 `MultiLevelCache`（PR #88 修复 `_setnxGate` 必须 `static`） |
| `Common/` | 通用工具 | `ApiResult<T>` (record) |
| `Cqrs/` | 自建 CQRS Dispatcher + Pipeline Behaviors | `ICommandDispatcher` + `LoggingBehavior` + `TransactionBehavior`（`CommandDispatcher` 是 `internal sealed class`，公共契约走 `ICommandDispatcher`） |
| `DataContext/` | EF Core DbContext 基类 + 拦截器 | `BaseDataContext`, `TenE0SystemDbContext`, `AuditInterceptor` |
| `DependencyInjection/` | DI 注册扩展方法（每个模块一个） | `AddTenE0Core()`, `AddTenE0Identity()` 等 |
| `DynamicFilters/` | 动态数据过滤规则引擎 + provider factory 描述符（PR #68） | `IDynamicFilterProvider`, `FilterExpressionBuilder`, `IDbProviderFactoryDescriptor` |
| `Entities/` | 实体基类层次 | `BaseEntity`, `TimedEntity`, `AuditedEntity`, `TreeAuditedEntity` |
| `EntityService/` | 通用 CRUD 服务（部分更新、唯一校验、字段权限、M:N 处理） | `IEntityService`, `EntityWriteOptions` |
| `Errors/` | 请求级错误收集 | `IErrs`（公开契约；`Errs` 是 `internal sealed class`，外部项目通过 `IErrs` 注入） |
| `Events/` | 领域事件基础 | `AggregateRoot`, `IDomainEvent`, `IDomainEventDispatcher`, `InProcessDomainEventDispatcher` |
| `Events/Outbox/` | Outbox Pattern 全套（PR #77/#85/#86） | `OutboxInterceptor`, `OutboxRelayService`, `IOutboxPublisher`, `IOutboxAdmin`, `OutboxSchemaSeeder`, `IOutboxLock` + 4 provider (`None`/`RowLock`/`Distributed`/`Leader`) |
| `Files/` | 文件上传/下载（本地/S3/OSS）+ 图片处理 | `IFileService`, `IFileStorage`, `ImageProcessor` |
| `Hosting/` | 启动期数据库初始化 | `DatabaseInitializerService` |
| `Json/` | JSON 序列化：PostedProperties 解析 + IOptions 注入 | `PostedBodyConvert`, `HttpRequestExtensions` |
| `Menus/` | 菜单树 CRUD + 角色分配 | `IMenuService`, `MenuService` |
| `Organizations/` | 组织架构树管理（物化路径） | `IOrgTreeService`, `OrgTreeService` |
| `Permissions/` | 权限评估 + 缓存 + 行级过滤 + 角色版本号 | `IPermissionEvaluator`, `PermissionCatalog` (sealed class), `IRoleVersionStore` (#7), `IEntityFilterContributor` |
| `Queries/` | 动态查询 + 分页 | `DynamicQueryExtensions`, `PagedQuery<T>` |
| `Sequences/` | 序列号生成器（支持日期重置） | `ISequenceGenerator`, `EfSequenceGenerator` |
| `Workflow/` | 轻量审批流引擎（状态机 + 流程定义 + 运行时，#156 epic） | `StateMachine<TState,TAction>`, `ProcessBuilder`, `IProcessRuntimeService`, `WorkflowEngine`, `TimeoutProcessor` |

## 设计原则

1. **不依赖 MediatR** — 自建轻量 CQRS，避免商业许可风险
2. **不依赖 E0Context 大杂烩** — 每个服务独立注入，可单独测试
3. **不依赖 MetaContext 反射缓存** — 改用 EF Core IModel 元数据
4. **实体标记接口驱动** — `ITimerEntity`、`ISoftDeleteEntity`、`ITreeEntity`、`IMultiTenantEntity` 自动被拦截器和 EF 配置处理
5. **M:N 用 Skip Navigation 自省** — 不再需要 `MultipleEntity` 标记基类
6. **字符串常量抽象成注入点** — `ITokenClaimNames` / `ErrorCodes` / `ICacheKeyNamespace`（PR #71）让业务项目可重命名 JWT claim 名、错误码、缓存 key 前缀而无需 fork。`ErrorCodes` 是 static 常量中心（与另外两个 interface+默认实现模式不同 —— 错误码无扩展需求）
7. **多级缓存与原子计数器抽抽象** — `IMultiLevelCache` + `IAtomicCounter`（PR #64 + **PR #88** 加纯读 `GetAsync` + SETNX 进程内 `_setnxGate` static 锁防 exactly-once 失败）让 Outbox 分布式锁 / 权限版本号等场景用同一套 Redis 原语；具体见 `Caching/CLAUDE.md`

## 框架实体（TenE0 前缀）

所有 `TenE0` 前缀的实体是框架自有表，业务项目不应直接修改：

`TenE0User`, `TenE0Role`, `TenE0UserRole`, `TenE0RefreshToken`, `TenE0RolePermission`, `TenE0Org`, `TenE0Sequence`, `TenE0Menu`, `TenE0RoleMenu`, `TenE0DataFilterRule`, `TenE0FileAttachment`, `TenE0ProcessDefinition`, `TenE0ProcessInstance`, `TenE0ProcessTask`, `TenE0ProcessHistory`, `OutboxMessage`（含 `LockedUntil` / `LockedByInstance` 列，PR #85）
