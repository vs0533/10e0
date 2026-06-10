# 10E0 (TenE0) — 下一代企业低代码框架

从 `code/E0.Core/` (.NET 6) 重构而来，命名空间 `TenE0.*`，目标框架 .NET 10。

## 仓库

- **GitHub**: https://github.com/vs0533/10e0 （私有仓库）
- **默认分支**: `dev`（开发集成分支，所有 feature PR 的目标）
- **发布分支**: `main`（仅接收 dev→main 合并，自动触发发版）

## 项目结构

```
10e0.slnx              — .NET 10 解决方案文件（slnx 格式）
Directory.Build.props   — 集中构建设置（net10.0, C#14, NRT, TreatWarningsAsErrors）
.editorconfig           — 代码风格规范（EnforceCodeStyleInBuild）

src/
├── 10E0.Api/    — HTTP API 层（Minimal API，应用入口 + Demo）
└── 10E0.Core/   — 共享框架核心（类库，NuGet 包: TenE0.Core）

tests/
├── 10E0.Api.Tests/    — Api 集成测试（xUnit + WebApplicationFactory）
└── 10E0.Core.Tests/   — Core 单元测试（xUnit + EF Core InMemory + coverlet）

.github/workflows/
├── pr-build.yml        — PR 构建测试 + 覆盖率
├── claude-review.yml   — Claude Code (MiniMax-M3) 自动审查
└── release.yml         — 自动发版（SemVer + GitHub Release + NuGet）
```

## 架构特征

- **Clean Architecture + DDD + CQRS**（自建 Dispatcher，不依赖 MediatR）
- **Pipeline Behavior 链**：Logging → Transaction → Permission → Handler（类 ASP.NET Core 中间件）
- **EF Core 10 + IDbContextFactory**：作用域工厂模式，支持并发查询
- **Outbox Pattern**：领域事件同事务落库 + 后台 Relay 异步发布
- **Named Query Filters**：软删除和行级数据过滤由 EF Core 自动注入

## 相对旧 E0.Core 的关键改进

| 改进 | 说明 |
|------|------|
| 去掉 MediatR | 自建 ICommandDispatcher，消除 12.x+ 商业许可风险 |
| 去掉 E0Context 大杂烩 | 拆为独立 DI 服务，可组合、可测试 |
| 去掉 MultipleEntity 基类 | M:N 改用 EF Core Skip Navigation 自省 |
| 修复 CommandManager 嵌套事务 Bug | TransactionBehavior 用 Savepoint 替代嵌套事务 |
| 去掉 MetaContext 反射缓存 | 改用 EF Core IModel 元数据 |
| 权限模型重构 | ControllTag+AccessCode → Permission Key + 分布式缓存 |

## 构建

```bash
dotnet build 10e0.slnx
dotnet test 10e0.slnx
```

## 运行

```bash
dotnet run --project src/10E0.Api
```

## CI/CD

### 工作流

| Workflow | 触发 | 说明 |
|----------|------|------|
| `pr-build.yml` | PR 到 dev/main | restore → build → test + coverage |
| `claude-review.yml` | PR opened/synchronize | 阿里云百炼 API (MiniMax-M3) headless 审查 |
| `release.yml` | push 到 main | 自动 patch+1 → tag → Release → NuGet pack |

### Code Review 配置

Claude Code CLI 以 headless 模式（`claude -p`）运行在 GitHub Actions，使用阿里云百炼 API 而非 Anthropic 官方：

```
ANTHROPIC_AUTH_TOKEN   → secrets.ALIBABA_API_KEY
ANTHROPIC_BASE_URL     → https://token-plan.cn-beijing.maas.aliyuncs.com/apps/anthropic
ANTHROPIC_MODEL        → qwen3.7-max
```

与本机 `~/.claude/settings.json` 中的配置一致。

### 发版流程

合并 PR 到 `main` 时自动触发：
1. 读最新 `v*` tag，patch+1（如 `v0.0.1` → `v0.0.2`）
2. `dotnet pack` TenE0.Core（注入版本号）
3. 创建 annotated tag + GitHub Release（附 .nupkg）
4. 可选发布到 NuGet.org（需 `NUGET_API_KEY`）

手动 bump major/minor：`git tag -a v2.0.0 -m "..." && git push origin v2.0.0`

### GitHub Secrets

| Secret | 用途 |
|--------|------|
| `ALIBABA_API_KEY` | 阿里云百炼 API 密钥（Code Review 必须） |
| `NUGET_API_KEY` | NuGet.org 发布密钥（可选） |

## 开发工具

- 项目使用 GitHub API MCP Server 管理仓库
- 使用前确保已配置 GitHub 认证（`gh auth login`）

## 目录说明

每个子目录都有独立的 `CLAUDE.md`，描述该模块的职责、设计决策和注意事项。
