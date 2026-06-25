# 变更记录

本项目所有重要变更都将记录在本文件中。

格式参考 [Keep a Changelog 1.1.0](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本 2.0.0](https://semver.org/lang/zh-CN/)。

## [Unreleased]

### Fixed

- **导入导出 Code Review 反馈修复** (#154 后续)：合并 PR #169 后 Code Review bot 标记的 🔴 Critical + 关键 🟡
  - 🔴 `DemoEntity.Code` 同时标 `[Sequence]` + `[ImportColumn]`：导入值会被序列生成覆盖，误导用户。改为 `[ImportIgnore]`，仅导出/模板，导入时由序列生成器自动填充
  - 🔴 `ExportStream.Content`（MemoryStream）经 `Results.File` 不释放：`ExportStream` 实现 `IDisposable` 持有所有权，Demo 端点改用 `RegisterForDispose` 释放，文档补资源释放说明
  - 🔴 `ImportResult.Empty` 共享可变单例（`Errors` 列表可被污染）：`Errors` 改 `IReadOnlyList<RowError>`，删除 `Empty`
  - 🟡 `RowError` 加默认 `Code = ImportRowError`，错误码实际接入（前端 i18n / 路由）
  - 🟡 导入端点加文件大小限制（50 MB + `RequestSizeLimitAttribute`），防超大文件 DoS
  - 🟡 `ExportFieldFilter.ShouldMask` 加 `ConcurrentDictionary` 结果缓存（大文件每单元格调用避免重复求值）
  - 🟡 删除 `ColumnMap` 上死代码 `#pragma CA1051`（全是属性不触发）
  - 🟡 事务模式副作用文档化：回滚仅撤销实体数据，不撤销已发布的领域事件 / 审计日志

### Added

- **通用 Excel/CSV 导入导出** (#154)：`TenE0.Core.ImportExport` 模块，企业应用 90% 业务模块需要的"列表导出 Excel" + "批量导入数据"开箱即用
  - 统一抽象 `IExcelExporter` / `IExcelImporter` / `ICsvExporter` / `ICsvImporter` / `IImportTemplateGenerator`
  - Excel 走 ClosedXML 0.105.0（MIT 许可，区别于 EPPlus 社区版的非商业条款），CSV 手写 RFC 4180 状态机（不引入 CsvHelper）
  - 声明式映射：attribute（`[ImportColumn]` / `[ExportColumn]` / `[ImportIgnore]` / `[ExportIgnore]`）+ fluent API（`ImportMapping<T>`），`MappingResolver` 合并 + 反射缓存
  - 框架协同：导入走 `IEntityService.CreateAsync`（复用唯一性 / 权限 / 流水号校验），导出接 `DynamicWhere` 查询
  - `ImportExecutor` 事务（全成功或全回滚）/ 非事务（部分失败收集错误继续）双模式 + `IProgress<ImportProgress>` 进度回调
  - 大文件：`IQueryable<T>` 流式分批加载（默认 5000/批）+ 超 `LargeExportThreshold`（默认 10w）自动降级 CSV（`ExportStream.Format` 标记）
  - 敏感字段脱敏：独立 `IExportFieldFilter`（默认包装 `IAuditFieldFilter`，审计未启用时直通），与审计脱敏解耦
  - DI 一行注册 `AddTenE0ImportExport()`（无 `<TContext>`，纯流处理）；Demo 端点 `GET /demo/export` / `POST /demo/import` / `GET /demo/import-template`
  - 新增错误码 `ImportRowError`（`IMPORT_ROW`）/ `ImportTransactionRolledback`（`IMPORT_ROLLBACK`）
  - 新增文档 `docs/22-import-export.md`
- **API 版本化（Asp.Versioning）** (#163)：`TenE0.Core.ApiVersioning` 模块，基于 Asp.Versioning v10 实现 API 多版本共存 + 每版本独立 OpenAPI 文档
  - **版本透明策略**：默认版本 `1.0`，未声明版本的请求按默认处理（`AssumeDefaultVersionWhenUnspecified=true`），既有裸路由端点零改动向后兼容
  - 三种版本声明方式并存：query string（`?api-version=1.0`，裸路由推荐）/ header（`X-Api-Version`）/ URL segment（需路由含 `{version:apiVersion}` 占位符）
  - `AddTenE0ApiVersioning()` 一行注册：版本读取器 + API Explorer（`GroupNameFormat = "'v'VVV"`）+ 版本感知 OpenAPI 文档生成
  - `MapTenE0OpenApi()` 包装 `MapOpenApi().WithDocumentPerVersion()`，Dev 环境 Scalar UI 按版本切换文档
  - 框架配置类 `ApiVersioningOptions` 与 Asp.Versioning 内部同名类冲突，DI 扩展用 `TenE0ApiVersioningOptions` 别名消歧 + `Configure<IServiceProvider>` 桥接
  - Demo 端点（全部）声明 v1.0 作为示范；其余端点（Auth/Admin/File/Workflow/Health）未版本化
  - 依赖：`Asp.Versioning.Http` + `Asp.Versioning.Mvc.ApiExplorer`（10.0.0 GA）+ `Asp.Versioning.OpenApi`（10.0.0-rc.1，受上游 [aspnetcore#66408](https://github.com/dotnet/aspnetcore/issues/66408) 阻塞，功能可用）
  - 新增文档 `docs/24-api-versioning.md`
- **多租户（Multi-Tenancy）** (#11)：业务实体实现 `IMultiTenantEntity` 后自动启用租户隔离
  - `IMultiTenantEntity` 接口（`TenantId` string 属性）
  - `ITenantContext` 抽象 + `HttpTenantContext` HTTP 实现（从 JWT `tenant_id` claim 读取）
  - `BaseDataContext.CurrentTenantId` + 自动注册 Named Query Filter `Tenant`，表达式 `BypassFilters || (e.TenantId == currentTenantId)`
  - `JwtClaims.TenantId` 常量（`"tenant_id"`）+ `IJwtTokenService.Issue` 新增 `tenantId` 参数
  - `TenE0User.TenantId` 字段 + `LoginCommandHandler` / `RefreshTokenCommandHandler` 透传写入 JWT
  - 新增文档 `docs/20-multi-tenancy.md`
- **`IRoleVersionStore` + 角色版本号** (#27)：`TenE0Role.Version` 字段（`long`，默认 1）；`PermissionGrantService.GrantAsync` / `RevokeAsync` / `SetGrantsAsync` 实际变更时自增；`IRoleVersionStore`（EF Core + IMemoryCache L1，5s TTL）按角色版本号快速检测权限变更；JWT 签发时把 `{roleCode: version}` 快照写入 `role_versions` claim，`PermissionEvaluator` 在 super-user 短路后比对 token vs DB
- **Outbox 多实例安全 — `IOutboxLock` + 4 种 provider** (#85, #86, #88)：
  - `IOutboxLock` 抽象（`TryAcquireAsync` / `ReleaseAsync`）+ `OutboxLockProviderKind` 枚举（`None` / `RowLock` / `Distributed` / `Leader`）
  - 4 种实现：`NoOpOutboxLock`（默认）/ `SqlServerOutboxLock`（`WITH (UPDLOCK, ROWLOCK, READPAST)`）/ `PostgresOutboxLock`（`SELECT ... FOR UPDATE SKIP LOCKED`）/ `DistributedOutboxLock`（基于 `IMultiLevelCache` L2 的应用层 SETNX）/ `LeaderElector`（全局只一个 Relay 抢主，其余空闲）
  - `OutboxMessage` 新增 `LockedUntil` + `LockedByInstance` 列（PR #85 DB schema 迁移）
  - `OutboxSchemaSeeder`（`IDataSeeder`，Order=0）：启动期幂等 ALTER 既有数据库补齐新列 + `(LockedUntil, OccurredOn)` 复合索引（SqlServer `sys.columns`/`sys.indexes` 探测，Postgres `information_schema` + `IF NOT EXISTS`）
  - `OutboxLockOptions` 兼容层（权威来源已迁移到 `OutboxRelayOptions`）
  - `OutboxLockProvider` 静态选择器 + `IOutboxRowLockResolver<TContext>` 契约 + `AddOutboxRowLock<TContext>()` DI 扩展
- **Outbox 死信管理（Poison Message）** (#77)：`IOutboxAdmin` 三操作契约 — `GetPoisonMessagesAsync` / `RetryPoisonMessageAsync` / `ExportPoisonMessagesAsync`；`OutboxAdminService<TContext>` 泛型实现；`OutboxPoisonMessageDto` 不可变 record；阈值复用 `OutboxRelayOptions.MaxAttempts`（禁止硬编码）；重置只清零 `AttemptCount` + `LastError`，`SentTime` 保持 null 让下轮 Relay 重新拾取
- **Outbox 真并发 exactly-once 验收** (#88)：`OutboxRelayConcurrencyTests` 用 `Testcontainers.MsSql` 启真实 SQL Server 2022 容器，两个独立 `IServiceProvider` + 共享 L2 缓存跑 30 轮 × 50 条消息并发，断言 Publisher 每条消息恰好被调 1 次（与 PR #88 同批：5 个 PR bot REQUEST_CHANGES 全部修完——`MultiLevelCache._setnxGate` static、`L1L2CacheForTest.TrySetAsync` 原子化、TestFakes helper、`DistributedOutboxLock.ReleaseAsync` no-op、TimeProvider 注册、SqlServer Container fixture）
- **`IMultiLevelCache` + `IAtomicCounter` 抽象** (#64)：L1 (进程内 `IMemoryCache`) + L2 (分布式 `IDistributedCache`) + 工厂回源；`GetOrSetAsync` / `TrySetAsync` (SETNX) / `SetAsync` (覆盖) / `RemoveAsync` / **`GetAsync`**（纯读，专为分布式锁 ownership 检查设计——`GetOrSetAsync` 在 L1+L2 miss 会调 factory 污染 L2，PR #88 教训）；`IAtomicCounter.IncrementAsync` 原子自增（Redis `INCR` / 内存 `Interlocked.Increment` / EF `UPDATE ... OUTPUT INSERTED.Value`）
- **全局字符串常量抽象为注入点** (#71)：`ITokenClaimNames` / `IErrorCodes` / `ICacheKeyNamespace` 让业务项目可重命名 JWT claim / 错误码 / 缓存 key 前缀而无需 fork 框架
- **EF 实体表名约定抽象** (#68)：`IDbProviderFactoryDescriptor` 替换 `AssemblyQualifiedName` 字典，让 DynamicFilters / DataContext 跨 EF provider（SqlServer / Npgsql / InMemory）的 metadata 探测更可控

### Changed

- `FileService` 泛型化为 `FileService<TContext>`（不再硬绑定 `TenE0SystemDbContext`），调用方需用 `AddTenE0Files<YourDbContext>()` 显式指定（见 [#6](https://github.com/vs0533/10e0/pull/6)）
- OSS / S3 Options 在构造期校验凭据（拒绝 `TODO`、`CHANGE_ME`、`PLACEHOLDER` 等占位符），错误消息指向环境变量名和 IAM / RAM Role 文档（[#6](https://github.com/vs0533/10e0/pull/6)）
- 缩略图命名从 `thumb_{file}` 改为 `{name}_thumb{ext}`，避免上传 `thumb.jpg` 时出现 `thumb_thumb.jpg` 这类前缀重复（[#6](https://github.com/vs0533/10e0/pull/6)）
- `IJwtTokenService.Issue` 新增 `roleVersions` 参数；`LoginCommandHandler` / `RefreshTokenCommandHandler` 在签发/刷新时查 `IRoleVersionStore` 拿当前快照写入 `role_versions` claim（[#27](https://github.com/vs0533/10e0/pull/27)）
- `PermissionEvaluator` 在 super-user 短路后插入 `DetectStaleRolesAsync`：对比 token vs DB，任一角色 `dbVersion > tokenVersion` 即视为 stale，绕过 `IPermissionCache` 走 `IPermissionStore` 重读（[#27](https://github.com/vs0533/10e0/pull/27)）
- **`IErrs` validation-error envelope 统一为 `ApiResult.Fail` shape** (#61)：所有 endpoint（替代 `IResponseEnvelope` / `ErrsResponse` 多形态）—— 让前端能用一个 union type 解构错误响应；`TenE0ExceptionHandler` 注入 `IOptions<JsonOptions>` (#66) 与 API 项目的 serializer 对齐，避免异常序列化格式漂移
- **`Program.cs` 拆分为模块化 endpoints/handlers/seeders** (#52)：原 ~500 行单体 → 多个职责单一的小文件（按域划分 endpoint、handler、seeder），新代码评审门槛大幅降低
- **`DbUpdateException` 在 `DefaultApiErrorMapper` 中消歧** (#55)：区分 concurrency conflict / unique violation / other，让前端能给出精确提示
- **`IValidateOptions<T>` 模式 + `DeleteResult` return type** (#67)：Files 模块 Options 校验改走 ASP.NET Core 标准 `IValidateOptions<T>` 管道（与 `JwtOptions` 等对齐）；`IFileStorage.DeleteAsync` 返回 `DeleteResult` enum（`Deleted` / `NotFound` / `Failed`）而非 `bool`，调用方可区分"不存在"和"删除失败"
- **`IAppModule` 契约** (#60)：下沉 `NullUserInfoLoader`；引入 `IAppModule` 模块装配契约（DI 扩展 + 模块元数据），让 `AddTenE0Xxx()` 各模块按统一接口注册——#43 大型重构前置
- **禁用 IDE0005 拦冗余 using** (#65)：CI 加 `dotnet_diagnostic.IDE0005.severity = error`，自动清存量 + 拦截新冗余

### Fixed

- **CQRS 并发与 Savepoint 边界** (#31)：`CommandDispatcher.WrapperCache` 改为 `internal` 移除反射访问；`TransactionBehavior` 嵌套 Savepoint 边界修正（100 批 × 16 并发，1600 个 GUID 全唯一）
- **`MultiLevelCache._setnxGate` 必须是 `static`** (#88 PR review)：实例锁不跨 `MultiLevelCache` 实例，导致并发 SETNX 路径串行化失败。同一类目还有 `L1L2CacheForTest.TrySetAsync` 必须原子化、`DistributedOutboxLock.ReleaseAsync` 必须 no-op、`LeaderElector.ReleaseAsync` 必须 no-op（per-message Release 触发 exactly-once 失败真 bug）
- **`OutboxRelayConcurrencyTests` BuildHost 缺 `AddTenE0Caching`** (#88)：原 fixture silent NoOp，导致 `IMultiLevelCache` 拿不到实例——直接 Assert.Fail 让"没真跑"显式暴露

### Security

- Refresh Token Rotation（OWASP 模式）：每次成功 refresh 旧 token 同事务撤销，新 token 写入；检测到旧 token 重放则撤销该用户全部活跃 token；新增 `JwtOptions.RefreshTokenRotationEnabled` 与 `SlidingRefreshExpiration`（默认均为 true）；`TenE0RefreshToken` 新增 `RevokedReason` 列（length 64）。需要 EF migration 升级 schema（[#6](https://github.com/vs0533/10e0/pull/6)）
- **Role version check（instant permission revocation）** (#27)：撤销某用户角色权限后，**下一个 HTTP 请求立即返回 403**（无需等待 access token 过期）。`HasAsync` 开销 < 5ms（5s L1 cache 命中）。关闭 #7 安全 HIGH-4 风险（撤销后 30 分钟权限仍可用的安全空窗）

### Dependencies

- 升级 `System.IdentityModel.Tokens.Jwt` 8.2.1 → 8.18.0（[#6](https://github.com/vs0533/10e0/pull/6)）
- 升级 `System.Linq.Dynamic.Core` 1.6.0 → 1.7.2（[#6](https://github.com/vs0533/10e0/pull/6)）
- 升级 `Aliyun.OSS.SDK.NetCore` 2.13.0 → 2.14.1（[#6](https://github.com/vs0533/10e0/pull/6)）
- 升级 `AWSSDK.S3` 3.7.305.7 → 4.0.23.6（v4 命名空间仍为 `Amazon.S3.*`），同时升级 `AWSSDK.Core` 3.7.x → 4.0.7.4（[#8](https://github.com/vs0533/10e0/issues/8)）
- 升级 `Microsoft.Data.Sqlite` → 10.0.0（#89 一并）
- **精确豁免 `NU1903`（GHSA-2m69-gcr7-jv3q）** (#63)：Restore 阶段出现的 advisory 级漏洞提示；用 `<NoWarn>NU1903</NoWarn>` 精准抑制，避免与 `Directory.Build.props` 的 `TreatWarningsAsErrors` 冲突导致全仓库 CI 阻塞

### Added (Tests)

- `AwsS3StorageTests` 13 个新单元测试，使用 Moq 注入 `IAmazonS3` 替身，覆盖 v4 API：构造期凭据/占位符校验、`StoreAsync` / `RetrieveAsync` / `DeleteAsync` / `ExistsAsync` / `GetAccessUrl` 正常 + 失败路径（[#8](https://github.com/vs0533/10e0/issues/8)）
- **Outbox 真并发验收集（Acceptance × 4 + Lock 单元 × N）** (#85, #86, #88)：`OutboxLockProviderSelectionTests`（×3 provider）/ `OutboxLockOptionsTests` / `LeaderElectorTests` / `DistributedOutboxLockTests` / `SqlServerOutboxLockTests` / `PostgresOutboxLockTests` / `OutboxAdminAcceptanceTests` / `OutboxLockAcceptanceTests` / `OutboxLockProviderAcceptanceTests` / `OutboxRelayLeaderElectionAcceptanceTests` / `OutboxRelayConcurrencyTests`（Requires=Docker）/ `OutboxSchemaSeederTests` / `TestFakes/InMemoryDistributedCache` + `L1L2CacheForTest` + `L2AtomicCounterForTest`
- **`10E0.Api.Tests` 扩量** (#70)：从 1 个占位测试 → ~50+ WebApplicationFactory 集成测试，覆盖 Minimal API 全部 endpoint（健康检查 / 认证 / CRUD / 权限）
- **CQRS 并发硬验收** (#31)：`CommandDispatcherTests.SendAsync_100Iterations_StableWrapperCache`（100 轮 wrapper cache 实例稳定）+ `TransactionBehaviorTests.ConcurrentNestedCommands_100Batches_AllSavepointNamesUnique`（100 批 × 16 并发，1600 个 savepoint GUID 全唯一）
- **Role version BDD acceptance 套件** (#27)：4 个 acceptance suite（874 行）覆盖 #7 role version 全链路 — `RoleVersionJwtClaimAcceptanceTests`（JWT sign/verify round-trip）/ `RoleVersionBumpAcceptanceTests`（EF InMemory 验证 grant/revoke bump）/ `RoleVersionCheckAcceptanceTests`（evaluator stale 检测 + legacy token + super-admin bypass）/ `RoleRevocationEndToEndAcceptanceTests`（WAF 端到端：admin revoke 后原 token 立即 403）
- **多租户 acceptance 套件** (#11)：4 个 suite 全链路覆盖
  - `MultiTenantEntityAcceptanceTests`（契约：`IMultiTenantEntity` 接口签名、`TenantId` 必填、`BaseEntity` 兼容）
  - `HttpTenantContextAcceptanceTests`（HTTP 实现：有 claim / 无 claim / 空字符串 / 未认证 / 同请求幂等回读）
  - `TenantIdJwtClaimAcceptanceTests`（JWT 签发：`LoginCommandHandler` 透传 `user.TenantId` → `tenant_id` claim / refresh 保留 / 无值时省略）
  - `TenantQueryFilterAcceptanceTests`（EF Core Query Filter：Named Query Filter 注册 / 跨租户自动隔离 / `IgnoreQueryFilters("Tenant")` 旁路 / 超管 `BypassFilters` 短路 / 与软删除/行级权限共存）

### Changed (Refactor)

- `AwsS3Storage` 新增 `(AwsS3Options, IAmazonS3)` 构造重载，支持客户端注入（之前硬编码 `new AmazonS3Client`，外部无法替身）。原 `IOptions<AwsS3Options>` 入口保持不变（[#8](https://github.com/vs0533/10e0/issues/8)）

### Infrastructure

- 新增 `LICENSE`（MIT）、`CHANGELOG.md`、`CONTRIBUTING.md`（[#6](https://github.com/vs0533/10e0/pull/6)）
- 新增 `dependabot.yml`：NuGet + GitHub Actions 周度更新，按 `Microsoft.*` / `AWSSDK.*` 分组减少 PR 噪音（[#6](https://github.com/vs0533/10e0/pull/6)）
- 新增 `codeql.yml`：csharp 推送 / PR / 周扫，manual build mode 适配 .NET 10 slnx（[#6](https://github.com/vs0533/10e0/pull/6)）
- PR build 加 `dotnet format --verify-no-changes` 门禁（[#6](https://github.com/vs0533/10e0/pull/6)）
- **`claude-review.yml` 加 `security-events: write` 权限** (#33)：GitHub auto-inject 的 "Perform CodeQL Analysis" step 需要此权限才能 upload SARIF 到 Security tab。修 PR #31/#32 偶发 Code Review job 失败的根因（GITHUB_TOKEN 临时刷新掩盖权限缺失，rerun 偶尔能过）
- **`claude-review.yml` review 提交重构** (#33)：从 `github.rest.pulls.createReview`（受 GITHUB_TOKEN 限制）改为 `fetch` + PAT header 直连 API；新增 self-approve 检测（PAT 账号 == PR 作者时自动降级为 comment）；diff 拉取从 `gh pr diff` 改为 `curl` GitHub API；移除 `push: branches: [feature/**]` 触发器（无 PR context 早退无产出）
- **`process-item.js` zh_CN locale + format gate** (#29)：所有 dotnet 命令强制加 `DOTNET_CLI_UI_LANGUAGE=en-US` 前缀（zh_CN locale 下 CLI 输出中文 "已通过!"，`Passed!` 匹配不到 → 误判失败）；TDD Verify 步加 `dotnet format --verify-no-changes --severity warn` 门禁
- **`process-item.js` schema 化 + inline review** (#32)：`BRANCHCHECK_SCHEMA` / `TESTS_SCHEMA` / `REVIEW_SCHEMA` 三个 JSON Schema 强制 agent 报结构化字段（修 w7bu0omg2 误报"在 dev 分支" + "Passed:" 数字匹配不到的根因）；`wait-for-pr-review` 子工作流改 inline agent（修 harness 单层嵌套限制）
- **`pr-build.yml` 加 workflow_dispatch + 跳过 Requires=Docker** (#88)：让 pr-build 跳过 Testcontainers 测试（`--filter "Requires!=Docker"`），由 `docker-integration-tests.yml` 单独跑；`workflow_dispatch` 允许手动 trigger
- **新增 `docker-integration-tests.yml`** (#88 + #89)：独立 workflow 跑 `[Trait("Requires", "Docker")]` 测试集（`Testcontainers.MsSql` 起 SQL Server 2022）；PR #89 增 `Pre-pull Docker images with retry` step（`testcontainers/ryuk:0.9.0` + `mcr.microsoft.com/mssql/server:2022-latest`，3 次重试 + 退避 10s/20s/30s，应对 Docker Hub 偶发 5xx/限流）；上传 `.trx`/`.html` + `/tmp/outbox-diag.txt` artifact 保留 14 天
- **`tests/10E0.Core.Tests/Events/Outbox/SqlServerContainerFixture.cs` 兼容 OrbStack macOS** (#89)：`TryResolveDockerEndpoint` 探测 4 路径（`DOCKER_HOST` env → `/private/var/run/docker.sock` 真 OrbStack socket → `~/.orbstack/run/docker.sock` → `/var/run/docker.sock` Docker Desktop），命中后注入 `DOCKER_HOST` env 让 Testcontainers 内部 Docker.DotNet 客户端走相同路径。macOS `/var/run/docker.sock` 是 dangling symlink，必须用 `/private/var/run/docker.sock`（`lsof -p OrbStack` 验证）
- **triage-loop / process-item 全自动合并到 dev** (#56, #57, #59, #62, #69, #72, #84)：自 2026-06-17 起，Merge & Sync 阶段在 CI 绿 + mergeable + bot VERDICT=APPROVE 时自动 `squash` 合并 PR 到 dev 并同步本地 dev。L2 plan-driven 多步 TDD（feature ≤5 文件/步、总步数 ≤6）/ L3 大 issue 自动拆分为 tracking epic（建 N 个 sub-issue + `epic` 标签 + checklist body）/ 自愈循环冲突门禁（CONFLICTING/DIRTY 立即停留人工，不空转）/ 防重复处理（PR 必须写 `Closes #<issue.id>` 否则 issue 遗留 open 被下轮重做，#51→#55→#58 真实教训）/ `dotnet format` 自动修复 + `--verify-no-changes` 确认（#49 案例：751 测试全过只卡 formatOk=false）/ baseSynced 硬校验（`git pull` 失败直接 throw，feature 分支基于过期 dev 撞 BEHIND/CONFLICTING 已修）/ 失败 item 改动 WIP 保护（catch 块先 `git add -A && commit && push` 保住改动）/ schema 化门禁字段（`buildOk` / `testsOk` / `formatOk` / `failed` / `passed` / `skipped`）
- **triage 自愈 / polish** (#75, #76, #78, #79, #83)：L2 plan-driven 实施 + L3 拆分健壮性 (#75) / 6 条 polish：makeEpic 拼原 body + splitInto null 兼容 + L3_REMEDIATE_SCHEMA 抽常量 + summary log + maxSteps 防御 + kebab-case 透传 (#76) / Watch Review 加 CI 硬门禁（CI 未全绿不允许返回 APPROVE）(#78) / L3 拆分崩溃修复 + 复杂 issue 处理能力增强（依赖顺序/深度限制/PR 路径/BDD 顺序）(#79) / Tests 门禁加自愈环节 + planner 判 L2 前核实项目前提 (#83) / Tests 加固正则兼容 .NET 10 多项目 `Passed N` 无冒号格式 (#84)

### Tests

- 行覆盖率 78.18% → 80%+；新增 N 个测试覆盖 CQRS 并发 / Savepoint 嵌套 / Refresh Token 旋转 / DI 扩展契约 / DynamicFilterProvider / BaseDataContext OnModelCreating / Outbox 全套 / 多租户 / Role version / API 集成（#6, #27, #31, #70, #85, #86, #88, #11）
- 测试依赖新增 `Microsoft.Data.Sqlite 10.0.0`（#89）

## [0.0.1] - 2026-06-02

10E0 (TenE0) 框架首个公开版本，从 `code/E0.Core/` (.NET 6) 重构而来，
命名空间统一为 `TenE0.*`，目标框架升级到 .NET 10 / C# 14。

### Added（首版核心特性）

- CQRS 自建 `ICommandDispatcher`（去 MediatR 商业许可风险）
- Pipeline Behaviors：Logging / Transaction（含 Savepoint 嵌套）/ Permission
- EntityService 通用 CRUD + Skip Navigation M:N 处理
- JWT 认证 + Refresh Token + 角色权限
- RBAC 权限系统：功能权限 + 字段级 + 行级数据过滤（三层粒度）
- 动态数据过滤引擎（运行时 JSON 规则）
- 文件服务（Local / Aliyun OSS / AWS S3）+ 图片处理
- Outbox Pattern 领域事件（同事务落库 + 后台 Relay 异步发布）
- 菜单 / 组织树 / 流水号
- EF Core 10 + `IDbContextFactory` + Named Query Filter

### Changed

- 框架命名空间从 `E0.Core` 改为 `TenE0.*`
- 目标框架升级到 .NET 10 / C# 14
- 解决方案文件改用 `.slnx` 新格式
- 启用 `TreatWarningsAsErrors` 和 `EnforceCodeStyleInBuild` 严格构建

### Documentation

- 完整中文文档 17 篇（`docs/01-architecture.md` → `docs/17-deployment.md`）+ 索引页
- 各模块独立 `CLAUDE.md`（架构职责、设计决策、注意事项）
- 顶层 `README.md` 重写，覆盖快速开始 / 项目结构 / CI 流程

### CI/CD

- `pr-build.yml`：PR 到 dev/main 自动 restore + build + test + 覆盖率门槛（80%）
- `claude-review.yml`：阿里云百炼 API（Qwen 3.7-max）headless 自动代码审查
- `release.yml`：push 到 main 自动 patch+1 → tag → GitHub Release → NuGet 打包

### Tests

- 覆盖率从 53% 提升到 80%（新增 97 个测试，见 [#2](https://github.com/vs0533/10e0/pull/2)）
- xUnit + EF Core InMemory + coverlet
- API 集成测试基于 `WebApplicationFactory`

[Unreleased]: https://github.com/vs0533/10e0/compare/v0.0.1...HEAD
[0.0.1]: https://github.com/vs0533/10e0/releases/tag/v0.0.1