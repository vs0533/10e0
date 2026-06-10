# 变更记录

本项目所有重要变更都将记录在本文件中。

格式参考 [Keep a Changelog 1.1.0](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本 2.0.0](https://semver.org/lang/zh-CN/)。

## [Unreleased]

### Added

- `IRoleVersionStore` 接口（`GetCurrentVersionsAsync`）+ `EfRoleVersionStore` 实现（EF Core + IMemoryCache L1，5s TTL），用于按角色版本号快速检测权限变更（[#27](https://github.com/vs0533/10e0/pull/27)）
- `TenE0Role.Version` 字段（`long`，默认 1）：`PermissionGrantService.GrantAsync` / `RevokeAsync` / `SetGrantsAsync` 实际变更时自增（[#27](https://github.com/vs0533/10e0/pull/27)）
- `JwtClaims.RoleVersion` 常量（`"role_versions"`）与 `ICurrentUserContext.RoleVersions` 属性：JWT 签发时把当前用户的 `{roleCode: version}` 快照写入 token；评估时按角色比对 token 快照 vs DB（[#27](https://github.com/vs0533/10e0/pull/27)）

### Changed

- `FileService` 泛型化为 `FileService<TContext>`（不再硬绑定 `TenE0SystemDbContext`），调用方需用 `AddTenE0Files<YourDbContext>()` 显式指定（见 [#6](https://github.com/vs0533/10e0/pull/6)）
- OSS / S3 Options 在构造期校验凭据（拒绝 `TODO`、`CHANGE_ME`、`PLACEHOLDER` 等占位符），错误消息指向环境变量名和 IAM / RAM Role 文档（[#6](https://github.com/vs0533/10e0/pull/6)）
- 缩略图命名从 `thumb_{file}` 改为 `{name}_thumb{ext}`，避免上传 `thumb.jpg` 时出现 `thumb_thumb.jpg` 这类前缀重复，且让缩略图与原图在文件列表中相邻排序（[#6](https://github.com/vs0533/10e0/pull/6)）
- `IJwtTokenService.Issue` 新增 `roleVersions` 参数；`LoginCommandHandler` / `RefreshTokenCommandHandler` 在签发/刷新时查 `IRoleVersionStore` 拿当前快照写入 `role_versions` claim（[#27](https://github.com/vs0533/10e0/pull/27)）
- `PermissionEvaluator` 在 super-user 短路后插入 `DetectStaleRolesAsync`：对比 token vs DB，任一角色 `dbVersion > tokenVersion` 即视为 stale，绕过 `IPermissionCache` 走 `IPermissionStore` 重读（[#27](https://github.com/vs0533/10e0/pull/27)）

### Security

- Refresh Token Rotation（OWASP 模式）：每次成功 refresh 旧 token 同事务撤销，新 token 写入；检测到旧 token 重放则撤销该用户全部活跃 token；新增 `JwtOptions.RefreshTokenRotationEnabled` 与 `SlidingRefreshExpiration`（默认均为 true）；`TenE0RefreshToken` 新增 `RevokedReason` 列（length 64）。需要 EF migration 升级 schema（[#6](https://github.com/vs0533/10e0/pull/6)）
- **Role version check（instant permission revocation）**：撤销某用户角色权限后，**下一个 HTTP 请求立即返回 403**（无需等待 access token 过期）。`HasAsync` 开销 < 5ms（5s L1 cache 命中）。关闭 #7 安全 HIGH-4 风险（撤销后 30 分钟权限仍可用的安全空窗）（[#27](https://github.com/vs0533/10e0/pull/27)）

### Dependencies

- 升级 `System.IdentityModel.Tokens.Jwt` 8.2.1 → 8.18.0（[#6](https://github.com/vs0533/10e0/pull/6)）
- 升级 `System.Linq.Dynamic.Core` 1.6.0 → 1.7.2（[#6](https://github.com/vs0533/10e0/pull/6)）
- 升级 `Aliyun.OSS.SDK.NetCore` 2.13.0 → 2.14.1（[#6](https://github.com/vs0533/10e0/pull/6)）

### Infrastructure

- 新增 `LICENSE`（MIT）、`CHANGELOG.md`、`CONTRIBUTING.md`（[#6](https://github.com/vs0533/10e0/pull/6)）
- 新增 `dependabot.yml`：NuGet + GitHub Actions 周度更新，按 `Microsoft.*` / `AWSSDK.*` 分组减少 PR 噪音（[#6](https://github.com/vs0533/10e0/pull/6)）
- 新增 `codeql.yml`：csharp 推送 / PR / 周扫，manual build mode 适配 .NET 10 slnx（[#6](https://github.com/vs0533/10e0/pull/6)）
- PR build 加 `dotnet format --verify-no-changes` 门禁（[#6](https://github.com/vs0533/10e0/pull/6)）

### Tests

- 行覆盖率 78.18% → 83.48%；新增 41 个测试覆盖 CQRS 并发 / Savepoint 嵌套 / Refresh Token 旋转 / DI 扩展契约 / DynamicFilterProvider / BaseDataContext OnModelCreating（[#6](https://github.com/vs0533/10e0/pull/6)）
- 测试依赖新增 `Microsoft.Data.Sqlite 10.0.0`（[#6](https://github.com/vs0533/10e0/pull/6)）
- 新增 4 个 BDD acceptance suite（874 行）覆盖 #7 role version 全链路：`RoleVersionJwtClaimAcceptanceTests`（JWT sign/verify round-trip）/ `RoleVersionBumpAcceptanceTests`（EF InMemory 验证 grant/revoke bump）/ `RoleVersionCheckAcceptanceTests`（evaluator stale 检测 + legacy token + super-admin bypass）/ `RoleRevocationEndToEndAcceptanceTests`（WAF 端到端：admin revoke 后原 token 立即 403）（[#27](https://github.com/vs0533/10e0/pull/27)）

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
