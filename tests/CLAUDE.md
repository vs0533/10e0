# tests/ — 测试项目

| 项目 | 引用 | 用途 |
|------|------|------|
| `10E0.Core.Tests` | 10E0.Core | Core 框架核心逻辑单元测试 |
| `10E0.Api.Tests` | 10E0.Api | Api 集成测试（WebApplicationFactory） |

## 运行

```bash
cd /Users/wilder/dev/10e0
dotnet test 10e0.slnx                          # 全部测试
dotnet test tests/10E0.Core.Tests              # 仅 Core 测试
dotnet test tests/10E0.Api.Tests               # 仅 Api 测试
dotnet test --collect:"XPlat Code Coverage"    # 带覆盖率
```

## 技术栈

- **xUnit** — 测试框架
- **coverlet** — 代码覆盖率收集
- **Microsoft.EntityFrameworkCore.InMemory** — EF Core 内存数据库（Core 测试）
- **Microsoft.AspNetCore.Mvc.Testing** — WebApplicationFactory（Api 集成测试）

## 当前状态

- **10E0.Core.Tests**: ~700 个测试，覆盖 Auth/Jwt、Cqrs、Permissions、Events/Outbox、DynamicFilters、EntityService、Files、Hosting、Json、Menus、Organizations、Queries、Sequences 等 17 个模块（含 Outbox 18 个测试文件 + TestFakes 真并发验收集）
- **10E0.Api.Tests**: ~50+ 集成测试，基于 `WebApplicationFactory` 覆盖 Minimal API 全部 endpoint（PR #70 扩量）
- **CI 覆盖率阈值**: 行 80%（pr-build 用 `/p:ThresholdLine=80` 门禁）

### 各模块测试文件

| 模块 | 测试文件 | 用例 |
|------|---------|------|
| Abstractions | EntityContracts, DefaultDataAccessPolicy | 2 |
| Auth | AuthCommands, HttpCurrentUserContext, AmbientCurrentUserContext | 3 |
| Auth/Jwt | JwtTokenService, Pbkdf2PasswordHasher | 2 |
| Auth/Jwt/Commands | LoginCommandHandler, RefreshTokenCommandHandler (rotation + sliding + reuse), LogoutCommandHandler | 3 |
| Auth/Jwt/Storage | AuthModelBuilderExtensions | 1 |
| Common | ApiResult | 1 |
| DataContext | BaseDataContextTests (OnModelCreating 各 filter 注册) | 13 |
| Cqrs | CommandDispatcher (基础 + 并发 + 边界) | 12 |
| Cqrs/Behaviors | TransactionBehavior (嵌套 Savepoint 边界), LoggingBehavior | 11 |
| DataContext/Interceptors | AuditInterceptor | 1 |
| DependencyInjection | PermissionsExtensionsTests (AddTenE0Permissions/Storage/FromAssembly) | 15 |
| DynamicFilters | FilterExpressionBuilder, ConditionRule, DataFilterRuleService, **DynamicFilterProvider (SQLite + DbProviderFactories)** | 13 |
| Entities | BaseEntity | 1 |
| EntityService | Create, Update, Delete, WriteOptions | 4 |
| EntityService/Relations | RelationProcessor | 1 |
| EntityService/Validators | FieldUnique, GroupUnique, UniqueFactory | 3 |
| Errors | Errs | 1 |
| Events | AggregateRoot, InProcessDomainEventDispatcher | 2 |
| Events/Outbox | OutboxInterceptor, InProcessOutboxPublisher, OutboxRelayService, **OutboxLockProviderSelection** (×3), **OutboxLockOptions**, **LeaderElector**, **DistributedOutboxLock**, **SqlServerOutboxLock**, **PostgresOutboxLock**, **OutboxAdmin**, **OutboxSchemaSeeder**, **+ 4 Acceptance (LockProvider / LeaderElection / DistributedLock / Admin)** | 18 |
| Files | FileService, ImageProcessor, FilesModels, FilesExtensions | 4 |
| Files/Storage | LocalFileStorage | 1 |
| Hosting | DatabaseInitializerService | 1 |
| Json | PostedBodyConvert, HttpRequestExtensions | 2 |
| Menus | MenuServiceCrud, MenuServiceQuery, MenuServiceStatic, MenuDtos | 4 |
| Organizations | OrgTreeService | 1 |
| Permissions | PermissionCatalog, PermissionEvaluator, RequirePermissionAttribute, DistributedPermissionCache | 4 |
| Permissions/Behaviors | PermissionBehavior, PermissionBdd | 2 |
| Permissions/Management | PermissionGrantService | 1 |
| Permissions/Storage | EfPermissionStore | 1 |
| Queries | DynamicQueryExtensions, PagedQuery | 2 |
| Sequences | SequenceFormat, EfSequenceGenerator | 2 |
| ModelBuilders | 统一 ModelBuilder 扩展测试 (6 个扩展方法) | 1 |

### 已知覆盖缺口（跳过，非高质量测试目标）

- `Files/Storage/AwsS3Storage` + `AliyunOssStorage` — 外部 SDK 依赖，需集成测试
- `DependencyInjection/` 其他扩展 — `CqrsServiceCollectionExtensions` / `JwtAuthExtensions` 等仍有缺口
- `TenE0*` 实体模型类 — 无业务逻辑的 POCO 属性定义

### Requires=Docker 测试（Outbox 真实并发验证）

`OutboxRelayConcurrencyTests` 与 Outbox 真实并发 Acceptance 套件需 **Docker daemon** —— 用 `Testcontainers.MsSql` 启真实 SQL Server 容器跑"两 Host + 50 条消息 + 30 轮" exactly-once 并发验证。

涉及文件：
- `OutboxRelayConcurrencyTests` —— 共享 L2 缓存的双 Host 跑 50 条 × 30 轮 exactly-once
- `OutboxRelayLeaderElectionAcceptanceTests` —— LeaderElector 抢主 / 续约 / 退位
- `OutboxLockAcceptanceTests` —— 行级锁 provider 全套路径
- `OutboxLockProviderAcceptanceTests` —— provider 选型（None / RowLock / Distributed / Leader）
- `OutboxAdminAcceptanceTests` —— 毒消息 Get / Retry / Export
- `DistributedOutboxLockAcceptanceTests` —— Distributed 锁 SETNX + GetAsync 纯读
- `SqlServerOutboxLockTests` / `PostgresOutboxLockTests` —— provider 单元测试

Fixture：`SqlServerContainerFixture`（共享 `xUnit IClassFixture`）—— 探测 4 路径 Docker socket（`DOCKER_HOST` env / `/private/var/run/docker.sock` OrbStack / `~/.orbstack/run/docker.sock` 旧 OrbStack / `/var/run/docker.sock` Docker Desktop），命中后注入 `DOCKER_HOST` env 让 Testcontainers 内部客户端走相同路径。macOS `/var/run/docker.sock` 是 dangling symlink，必须用 `/private/var/run/docker.sock`（PR #89 教训）。

| 情况 | 行为 |
|---|---|
| 本地有 Docker（Docker Desktop / OrbStack / colima 任一 daemon 在线） | `dotnet test` 跑全部测试，包括这些 → 真验证跨进程 SETNX / 续约 / Leader 抢主 |
| 本地无 Docker | 测试**显式 fail**（`Assert.Fail`，不静默 return — PR #88 教训）—— 提醒开发者"这些测试没真跑" |
| CI PR 普通 build | `pr-build.yml` 走 `--filter "Requires!=Docker"` 跳过 |
| CI Requires=Docker 专项 | `docker-integration-tests.yml` workflow 单独跑（ubuntu-latest 自带 Docker；预拉镜像 + 3 次重试应对 Docker Hub 偶发 5xx） |

需要本地跑：装 [Docker Desktop](https://www.docker.com/products/docker-desktop/) 或 [OrbStack](https://orbstack.dev/) 让 daemon 起来，然后 `dotnet test`。
无 Docker 又想跑其它测试：`dotnet test --filter "Requires!=Docker"`。

## 测试依赖

| 包 | 版本 | 用途 |
|----|------|------|
| `Microsoft.Data.Sqlite` | 10.0.0 | `DynamicFilterProvider` 测试（真实 SQLite 连接 + DbProviderFactories） |
