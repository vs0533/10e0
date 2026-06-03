# 变更记录

本项目所有重要变更都将记录在本文件中。

格式参考 [Keep a Changelog 1.1.0](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本 2.0.0](https://semver.org/lang/zh-CN/)。

## [Unreleased]

### Added

- 暂无

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

- 覆盖率从 53% 提升到 80%（新增 97 个测试）
- xUnit + EF Core InMemory + coverlet
- API 集成测试基于 `WebApplicationFactory`

[Unreleased]: https://github.com/vs0533/10e0/compare/v0.0.1...HEAD
[0.0.1]: https://github.com/vs0533/10e0/releases/tag/v0.0.1
