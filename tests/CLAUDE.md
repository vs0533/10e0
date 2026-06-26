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
- **Testcontainers.MsSql** — 真实 SQL Server 容器（Requires=Docker Acceptance）

## 当前状态

- **10E0.Core.Tests**: ~930 个测试方法 / 109 个 .cs 文件 / 21 个模块目录
- **10E0.Api.Tests**: ~35 个集成测试 / 6 个 .cs 文件（含 CentralizedExceptionHandling / Issue14Coverage / RoleRevocationEndToEnd / UnifiedValidationEnvelope 4 个 Acceptance + Placeholder + GlobalUsings）
- **Acceptance Tests 跨全仓**: 25 个 `*AcceptanceTests.cs`（Api.Tests 4 + Core.Tests 21）
- **CI 覆盖率阈值**: 行 80%（pr-build 用 `/p:ThresholdLine=80` 门禁；阈值由 pr-build.yml 控制，本文档不重复最近一次实测数字）

## 测试文件清单

> 含 `GlobalUsings.cs`（×2）与 `PlaceholderTests.cs`（×2）等骨架文件未单列。下表只列实际承载测试方法的文件，按模块目录组织。

| 模块 | 测试文件 |
|------|----------|
| Abstractions | `EntityContracts`, `DefaultDataAccessPolicy`, `MultiTenantEntityAcceptanceTests`, `TokenClaimNames`, `ErrorCodes`, `CacheKeyNamespace`, `TenantContext` |
| Auth | `AuthCommands`, `HttpCurrentUserContext`, `AmbientCurrentUserContext`, `NullUserInfoLoader` |
| Auth/Jwt | `JwtTokenService`, `Pbkdf2PasswordHasher` |
| Auth/Jwt/Commands | `LoginCommandHandler`, `RefreshTokenCommandHandler` (rotation + sliding + reuse), `LogoutCommandHandler` |
| Auth/Jwt/Storage | `AuthModelBuilderExtensions` |
| Caching | `DefaultCachingImplementations` |
| Common | `ApiResult` |
| DataContext | `BaseDataContextTests` (OnModelCreating 各 filter 注册) |
| Cqrs | `CommandDispatcher` (基础 + 并发 + 边界) |
| Cqrs/Behaviors | `TransactionBehavior` (嵌套 Savepoint 边界), `LoggingBehavior` |
| DataContext/Interceptors | `AuditInterceptor` |
| DependencyInjection | `PermissionsExtensionsTests` (AddTenE0Permissions/Storage/FromAssembly) |
| DynamicFilters | `FilterExpressionBuilder`, `ConditionRule`, `DataFilterRuleService`, `DynamicFilterProvider` (SQLite + DbProviderFactories) |
| Entities | `BaseEntity` |
| EntityService | `Create`, `Update`, `Delete`, `WriteOptions` |
| EntityService/Relations | `RelationProcessor` |
| EntityService/Validators | `FieldUnique`, `GroupUnique`, `UniqueFactory` |
| Errors | `Errs` |
| Events | `AggregateRoot`, `InProcessDomainEventDispatcher` |
| Events/Outbox | `OutboxInterceptor`, `InProcessOutboxPublisher`, `OutboxRelayService`, `OutboxLockProviderSelection`, `OutboxLockOptions`, `LeaderElector`, `DistributedOutboxLock`, `SqlServerOutboxLock`, `PostgresOutboxLock`, `OutboxAdmin`, `OutboxSchemaSeeder`, **`SqlServerContainerFixture`** (×1)，**+ TestFakes/** 子目录：`InMemoryDistributedCache` (+ 自测) / `L1L2CacheForTest` / `L2AtomicCounterForTest`；**+ Acceptance (×4)**：`OutboxLockAcceptanceTests` / `OutboxLockProviderAcceptanceTests` / `OutboxRelayLeaderElectionAcceptanceTests` / `OutboxAdminAcceptanceTests` / `DistributedOutboxLockAcceptanceTests` |
| Files | `FileService`, `ImageProcessor`, `FilesModels`, `FilesExtensions` |
| Files/Storage | `LocalFileStorage` |
| Hosting | `DatabaseInitializerService` |
| Json | `PostedBodyConvert`, `HttpRequestExtensions` |
| Menus | `MenuServiceCrud`, `MenuServiceQuery`, `MenuServiceStatic`, `MenuDtos` |
| Organizations | `OrgTreeService` |
| Permissions | `PermissionCatalog`, `PermissionEvaluator`, `RequirePermissionAttribute`, `DistributedPermissionCache`, `IRoleVersionStore` (#7), `RoleVersionBump`, `RoleVersionCheck`, `RoleVersionJwtClaim`, `RoleRevocationEndToEnd` |
| Permissions/Behaviors | `PermissionBehavior`, `PermissionBdd` |
| Permissions/Management | `PermissionGrantService` |
| Permissions/Storage | `EfPermissionStore` |
| Queries | `DynamicQueryExtensions`, `PagedQuery` |
| Sequences | `SequenceFormat`, `EfSequenceGenerator` |
| 顶层 | `ModelBuilderExtensions` (统一 ModelBuilder 扩展测试) |

> **用例数（`[Fact]` / `[Theory]` 方法数）此处不维护**——这些数字随每次 PR 改动漂移很快，不验证就必然撒谎；测试文件清单本身已足够定位测试覆盖。

## 已知覆盖缺口（跳过，非高质量测试目标）

- `Files/Storage/AwsS3Storage` + `AliyunOssStorage` — 外部 SDK 依赖，需集成测试
- `DependencyInjection/` 其他扩展 — `CqrsServiceCollectionExtensions` / `JwtAuthExtensions` 等仍有缺口
- `TenE0*` 实体模型类 — 无业务逻辑的 POCO 属性定义

## Requires=Docker 测试（Outbox 真实并发验证）

> **重要**：CI 由 `.github/workflows/docker-integration-tests.yml` 跑 Requires=Docker trait 的测试。**实际只有 `OutboxRelayConcurrencyTests` 标了 `[Trait("Requires", "Docker")]`**（fixture 类级别 trait 自动继承给引用 fixture 的测试类）。其余 Acceptance（`OutboxRelayLeaderElectionAcceptanceTests` / `OutboxLockAcceptanceTests` / `OutboxLockProviderAcceptanceTests` / `OutboxAdminAcceptanceTests` / `DistributedOutboxLockAcceptanceTests`）当前未加 trait，CI 跳过。如需它们走 Requires=Docker workflow，需补 trait 标记（单独开 issue）。

具体行为：

| 情况 | 行为 |
|---|---|
| 本地有 Docker（Docker Desktop / OrbStack / colima 任一 daemon 在线） | `dotnet test` 跑全部测试，包括 Requires=Docker |
| 本地无 Docker | 测试**显式 fail**（`Assert.Fail`，不静默 return — PR #88 教训）—— 提醒开发者"这些测试没真跑" |
| CI PR 普通 build | `pr-build.yml` 走 `--filter "Requires!=Docker"` 跳过 |
| CI Requires=Docker 专项 | `docker-integration-tests.yml` workflow 单独跑（ubuntu-latest 自带 Docker；预拉镜像 + 3 次重试应对 Docker Hub 偶发 5xx） |

### SqlServerContainerFixture（shared `xUnit IClassFixture`）

探测 4 路径 Docker socket（`DOCKER_HOST` env / `/private/var/run/docker.sock` OrbStack / `~/.orbstack/run/docker.sock` 旧 OrbStack / `/var/run/docker.sock` Docker Desktop），命中后注入 `DOCKER_HOST` env 让 Testcontainers 内部客户端走相同路径。macOS `/var/run/docker.sock` 是 dangling symlink，必须用 `/private/var/run/docker.sock`（PR #89 教训，`lsof -p OrbStack` 验证）。

### Outbox TestFakes 子目录

`tests/10E0.Core.Tests/Events/Outbox/TestFakes/` 三个并发测试 Fake：

- `InMemoryDistributedCache` + 自测 — 进程内 `IDistributedCache` 实现，支持 L1 SETNX 原子化
- `L1L2CacheForTest` — L1+L2 双层 fake cache，让 Outbox 真实并发测试无需 Redis
- `L2AtomicCounterForTest` — 进程内 `IAtomicCounter` fake

需要本地跑：装 [Docker Desktop](https://www.docker.com/products/docker-desktop/) 或 [OrbStack](https://orbstack.dev/) 让 daemon 起来，然后 `dotnet test`。
无 Docker 又想跑其它测试：`dotnet test --filter "Requires!=Docker"`。

## 测试依赖

| 包 | 版本 | 用途 |
|----|------|------|
| `Microsoft.Data.Sqlite` | 10.0.0 | `DynamicFilterProvider` 测试（真实 SQLite 连接 + DbProviderFactories） |
| `Testcontainers.MsSql` | latest | Requires=Docker Acceptance（启真实 SQL Server 2022 容器） |
| `Docker.DotNet` | latest | fixture 探测本机 Docker daemon（OrbStack socket 探测） |