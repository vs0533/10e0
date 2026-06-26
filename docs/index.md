# 10E0 — 下一代企业低代码框架

基于 **.NET 10 / C# 14**，采用 **Clean Architecture + DDD + CQRS** 架构，从旧 `E0.Core` (.NET 6) 完全重构而来。NuGet 包名：`TenE0.Core`。

---

## 特性亮点

- **自建 CQRS Dispatcher** — 不依赖 MediatR，消除 12.x+ 商业许可风险。`ICommandDispatcher` 零反射分发（`IQuery` 是语义别名，共用一个分发器）
- **Pipeline Behavior 链** — Logging → Transaction → Permission → Handler，类 ASP.NET Core 中间件，可插拔组合
- **EF Core 10 + IDbContextFactory** — 作用域工厂模式，支持并发查询。Named Query Filters 自动注入软删除和行级数据过滤
- **Outbox Pattern** — 领域事件同事务落库，后台 Relay 异步发布，确保最终一致性
- **RBAC 权限系统** — 角色 + 权限 Key 两级模型，支持字段级和行级数据权限，分布式缓存加速
- **动态查询引擎** — 基于字符串表达式（编译为 LINQ Expression）的动态过滤、排序、分页，参数化防注入
- **多后端文件服务** — 统一 `IFileService` 接口，支持本地文件系统、阿里云 OSS、AWS S3，自动缩略图与水印
- **物化路径树** — 组织架构和菜单树使用物化路径（Materialized Path）方案，支持无限层级、高效子树查询和移动

---

## 文档导航

| 文档 | 描述 |
|------|------|
| [01-architecture](01-architecture.md) | 整体架构、项目结构、技术栈 |
| [02-quickstart](02-quickstart.md) | 5 分钟快速开始，从新建项目到跑通 API |
| [03-di-setup](03-di-setup.md) | DI 注册完整参考，所有服务的注册方式 |
| [04-cqrs](04-cqrs.md) | CQRS 命令查询分离，Dispatcher 用法 |
| [05-entities](05-entities.md) | 实体模型与基类，约定与规范 |
| [06-entity-service](06-entity-service.md) | EntityService 通用 CRUD，零代码增删改查 |
| [07-data-context](07-data-context.md) | DataContext 与 EF Core，连接管理 |
| [08-auth-jwt](08-auth-jwt.md) | JWT 认证，Token 签发与验证 |
| [09-permissions](09-permissions.md) | 权限系统，RBAC + 字段级 + 行级 |
| [10-domain-events](10-domain-events.md) | 领域事件与 Outbox 机制 |
| [11-dynamic-filters](11-dynamic-filters.md) | 动态数据过滤规则，Named Query Filters |
| [12-files](12-files.md) | 文件上传与多后端存储 |
| [13-organizations](13-organizations.md) | 组织架构树，物化路径方案 |
| [14-menus](14-menus.md) | 菜单管理，动态菜单树 |
| [15-sequences](15-sequences.md) | 流水号生成器，自定义编号规则 |
| [16-dynamic-queries](16-dynamic-queries.md) | 动态查询与分页，表达式树查询 |
| [17-deployment](17-deployment.md) | 部署与 CI/CD 配置 |
| [18-sync-pr-strategy](18-sync-pr-strategy.md) | dev → main 同步 PR 合并策略（为何禁 Squash） |
| [19-sync-retrospective](19-sync-retrospective.md) | 2026-06 同步事故复盘：教训、安全底线、行业做法 |
| [20-multi-tenancy](20-multi-tenancy.md) | 多租户隔离（`IMultiTenantEntity` + Named Query Filter + JWT tenant claim） |
| [21-workflow](21-workflow.md) | 轻量审批流引擎（状态机 + 流程定义 + 流程运行时） |
| [22-import-export](22-import-export.md) | 通用 Excel/CSV 导入导出（ClosedXML + RFC 4180 + ImportExecutor） |
| [23-realtime](23-realtime.md) | 声明式实时推送（SignalR + INotifyClient + org claim 链路） |
| [24-api-versioning](24-api-versioning.md) | API 版本化（Asp.Versioning + 版本透明 + 每版本 OpenAPI 文档） |
| [25-security](25-security.md) | 安全防刷三件套（限流 + 登录失败锁定 + 验证码） |
| [26-observability](26-observability.md) | 可观测性（HealthChecks + Metrics + OpenTelemetry，Core 零新依赖） |

---

## 快速链接

- **GitHub**: [github.com/vs0533/10e0](https://github.com/vs0533/10e0)
- **NuGet**: `dotnet add package TenE0.Core`
- **License**: MIT
