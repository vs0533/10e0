# 10E0.Core — 框架核心库

命名空间 `TenE0.Core.*`，所有框架功能的承载体。被 10E0.Api 引用。

## 模块一览

| 模块目录 | 职责 | 核心接口/类 |
|----------|------|-------------|
| `Abstractions/` | 全局契约：命令、实体标记、用户上下文、错误收集、租户、token/缓存 key 抽象 | `ICommand<T>`, `IBaseEntity`, `IMultiTenantEntity`, `ITenantContext`, `ITokenClaimNames`, `ICacheKeyNamespace`, `IErrs` |
| `Auth/` | 当前用户上下文实现（HTTP / Ambient）+ 租户上下文 | `HttpCurrentUserContext`, `AmbientCurrentUserContext`, `HttpTenantContext` |
| `Auth/Jwt/` | JWT 认证全流程 | `JwtTokenService`, `LoginCommandHandler` |
| `Caching/` | 多级缓存抽象 + 原子计数器（PR #64） | `IMultiLevelCache`, `IAtomicCounter` |
| `Common/` | 通用工具 | `ApiResult<T>` |
| `Cqrs/` | 自建 CQRS Dispatcher + Pipeline Behaviors | `CommandDispatcher`, `LoggingBehavior`, `TransactionBehavior` |
| `DataContext/` | EF Core DbContext 基类 + 拦截器 | `BaseDataContext`, `TenE0SystemDbContext`, `AuditInterceptor` |
| `DependencyInjection/` | DI 注册扩展方法（每个模块一个） | `AddTenE0Core()`, `AddTenE0Identity()` 等 |
| `DynamicFilters/` | 动态数据过滤规则引擎 + provider factory 描述符（PR #68） | `IDynamicFilterProvider`, `FilterExpressionBuilder`, `IDbProviderFactoryDescriptor` |
| `Entities/` | 实体基类层次 | `BaseEntity`, `TimedEntity`, `AuditedEntity`, `TreeAuditedEntity` |
| `EntityService/` | 通用 CRUD 服务（部分更新、唯一校验、字段权限、M:N 处理） | `IEntityService`, `EntityWriteOptions` |
| `Errors/` | 请求级错误收集 | `Errs : IErrs` |
| `Events/` | 领域事件 + Outbox Pattern 全套（PR #77/#85/#86/#88） | `AggregateRoot`, `OutboxInterceptor`, `OutboxRelayService`, `IOutboxLock`, `IOutboxAdmin`, `IOutboxPublisher`, `OutboxSchemaSeeder` |
| `Files/` | 文件上传/下载（本地/S3/OSS）+ 图片处理 | `IFileService`, `IFileStorage`, `ImageProcessor` |
| `Hosting/` | 启动期数据库初始化 | `DatabaseInitializerService` |
| `Json/` | JSON 序列化：PostedProperties 解析 + IOptions 注入 | `PostedBodyConvert`, `HttpRequestExtensions` |
| `Menus/` | 菜单树 CRUD + 角色分配 | `IMenuService`, `MenuService` |
| `Organizations/` | 组织架构树管理（物化路径） | `IOrgTreeService`, `OrgTreeService` |
| `Permissions/` | 权限评估 + 缓存 + 行级过滤 + 角色版本号 | `IPermissionEvaluator`, `PermissionCatalog`, `IRoleVersionStore`, `IEntityFilterContributor` |
| `Queries/` | 动态查询 + 分页 | `DynamicQueryExtensions`, `PagedQuery<T>` |
| `Sequences/` | 序列号生成器（支持日期重置） | `ISequenceGenerator`, `EfSequenceGenerator` |

## 设计原则

1. **不依赖 MediatR** — 自建轻量 CQRS，避免商业许可风险
2. **不依赖 E0Context 大杂烩** — 每个服务独立注入，可单独测试
3. **不依赖 MetaContext 反射缓存** — 改用 EF Core IModel 元数据
4. **实体标记接口驱动** — `ITimerEntity`、`ISoftDeleteEntity`、`ITreeEntity`、`IMultiTenantEntity` 自动被拦截器和 EF 配置处理
5. **M:N 用 Skip Navigation 自省** — 不再需要 `MultipleEntity` 标记基类
6. **字符串常量抽象成注入点** — `ITokenClaimNames` / `IErrorCodes` / `ICacheKeyNamespace`（PR #71）让业务项目可重命名 JWT claim 名、错误码、缓存 key 前缀而无需 fork
7. **多级缓存与原子计数器抽抽象** — `IMultiLevelCache` + `IAtomicCounter`（PR #64）让 Outbox 分布式锁 / 权限版本号等场景用同一套 Redis 原语

## 框架实体（TenE0 前缀）

所有 `TenE0` 前缀的实体是框架自有表，业务项目不应直接修改：

`TenE0User`, `TenE0Role`, `TenE0UserRole`, `TenE0RefreshToken`, `TenE0RolePermission`, `TenE0Org`, `TenE0Sequence`, `TenE0Menu`, `TenE0RoleMenu`, `TenE0DataFilterRule`, `TenE0FileAttachment`, `OutboxMessage`（含 `LockedUntil` / `LockedByInstance` 列，PR #85）
