# 部署与 CI/CD

10E0 使用 GitHub Actions 实现持续集成和自动发版。本文档涵盖本地构建、测试、NuGet 打包以及三个 CI/CD 工作流的详细说明。

---

## 前置条件

| 依赖 | 版本要求 |
|------|----------|
| .NET SDK | **10.0.x**（[下载](https://dotnet.microsoft.com/download/dotnet/10.0)） |
| IDE | VS Code + C# Dev Kit / Rider / Visual Studio 2022+ |

验证安装：

```bash
dotnet --version
# 应输出 10.0.x
```

---

## 本地构建

```bash
dotnet build 10e0.slnx
```

默认使用 `Debug` 配置。生产构建推荐使用 `Release`：

```bash
dotnet build 10e0.slnx -c Release
```

---

## 本地测试

```bash
dotnet test 10e0.slnx
```

测试项目使用 xUnit + coverlet，支持覆盖率门禁。启用在本地验证 80% 行覆盖率门槛：

```bash
dotnet test 10e0.slnx \
  -c Release \
  /p:CollectCoverage=true \
  /p:CoverletOutputFormat=cobertura \
  /p:ThresholdLine=80
```

---

## NuGet 打包

核心类库 `TenE0.Core` 通过 `dotnet pack` 打包为 NuGet 包：

```bash
dotnet pack src/10E0.Core/10E0.Core.csproj \
  -c Release \
  -o ./nupkgs \
  /p:Version=<版本号>
```

版本号遵循 [SemVer 2.0](https://semver.org/) 规范（`MAJOR.MINOR.PATCH`）。

---

## CI/CD 工作流

仓库包含三个 GitHub Actions 工作流，均在 `.github/workflows/` 下定义。

### 1. `pr-build.yml` — PR 构建测试

- **触发条件**：向 `dev` 或 `main` 提 PR、push 到 `test/**` 分支
- **并发控制**：同 PR 的旧运行自动取消
- **执行步骤**：
  1. `actions/checkout@v4`
  2. 安装 .NET 10 SDK
  3. `dotnet restore 10e0.slnx` — 还原依赖
  4. `dotnet build 10e0.slnx --no-restore -c Release` — 发布构建
  5. `dotnet test` — 运行测试，**强制行覆盖率 ≥ 80%**（`/p:ThresholdLine=80`），低于门槛则构建失败
  6. 上传测试结果（.trx / .html）和覆盖率报告（Cobertura XML），保留 14 天
- **所需权限**：`contents: read`（最小权限原则）

### 2. `claude-review.yml` — AI 代码审查

- **触发条件**：PR 的 `opened` 或 `synchronize` 事件，目标分支为 `dev` 或 `main`；另保留 `workflow_dispatch` 作应急手动触发
- **触发器注意**：~~`push: branches: [feature/**]`~~ 触发器已移除（PR #33）—— feature 分支 push 没 PR context，step 8 走早退；重复跑无产出浪费 CI
- **执行步骤**：
  1. 检出完整 git 历史（`fetch-depth: 0`）
  2. 安装 Node.js 22 和 `@anthropic-ai/claude-code` CLI
  3. 生成 PR 的 diff 文件：
     - **PR 触发**：通过 `curl` + `Authorization: Bearer ${{ github.token }}` 调 GitHub API `pulls/{n}` 拿 diff（PR #33，不依赖 gh CLI 认证）
     - **workflow_dispatch fallback**：`git diff origin/$BASE...HEAD` 兜底
  4. 将 diff 传入 Claude Code CLI，以 **headless 模式**（`claude -p`）审查
  5. 审查结果作为 PR Review 或 Comment 发布，分三个等级：
     - 🔴 **Critical** — 必须修复（Bug、安全、模式违规）
     - 🟡 **Suggestion** — 建议改进
     - 🟢 **Nit** — 可选微调
- **AI 后端**：阿里云百炼 API（**MiniMax-M3** headless mode），非 Anthropic 官方
- **超时**：15 分钟 review wait（`process-item.js` 的 `reviewTimeoutMs`）
- **review 提交路径**（PR #33 重构）：
  1. 优先用 **PAT**（`secrets.REVIEW_BOT_TOKEN`）调 `pulls.createReview`（`fetch` + PAT header 直连 API，不走 `github` client）—— 算 branch protection approval
  2. **Self-approve 检测**：若 PAT 账号 == PR 作者，GitHub 会拒绝 "can not approve your own pull request"，自动降级为 comment
  3. **降级 fallback**：用 `GITHUB_TOKEN` client (`github.rest.issues.createComment`) 发 comment —— **不计入** branch protection 批准
- **CodeQL 权限要求**（PR #33 新增）：workflow-level `permissions:` 必须 grant `security-events: write`，否则 GitHub auto-inject 的 "Perform CodeQL Analysis" step 会因缺权限无法 upload SARIF 到 Security tab（症状：Code Review job 偶发失败，rerun 偶尔能过，根因是 token 临时刷新掩盖了权限缺失）
- **容错**：`continue-on-error: true`，API 故障不阻塞 PR 合并

### 3. `release.yml` — 自动发版

- **触发条件**：push 到 `main` 分支（即 PR 合并到 `main` 时）
- **并发控制**：`cancel-in-progress: false`，防止发版被中途取消
- **执行步骤**：
  1. 检出完整历史（含所有 tag）
  2. 安装 .NET 10 SDK，执行 `restore` + `build`
  3. 运行测试（`--no-build`）
  4. **计算版本号**：从最新 `v*` tag 中读取，自动 **patch+1**（如 `v0.0.1` → `v0.0.2`）
  5. **NuGet 打包**：`dotnet pack src/10E0.Core/`，注入计算出的版本号
  6. **生成 Changelog**：按 `feat` / `fix` / other 分类提取 commit 信息
  7. **创建 annotated Git tag** 并推送到 origin
  8. **创建 GitHub Release**，附加 .nupkg 和 .snupkg 文件
  9. **发布到 NuGet.org**（仅当 `NUGET_API_KEY` secret 已配置时执行）
- **所需权限**：`contents: write`

---

## 发版流程

### 自动发版（默认）

合并 PR 到 `main` 即触发 `release.yml`：

```
dev 分支 → PR → 合并到 main → release.yml
                               ├─ patch+1 版本
                               ├─ git tag v0.0.x
                               ├─ GitHub Release
                               └─ NuGet.org（可选）
```

### 手动提升 Major / Minor

当需要非 patch 版本的升级（如大功能发布、破坏性变更），在本地执行：

```bash
# 手动创建 tag 并推送
git tag -a v1.0.0 -m "Release v1.0.0 - major feature drop"
git push origin v1.0.0
```

推送 tag 不会触发 `release.yml`（只有 push 到 `main` 才会触发），但会自动被后续发版流程检测为最新版本基线。如果想直接发布，需手动 push 到 `main` 或通过 GitHub Releases 页面创建。

---

## GitHub Secrets

| Secret | 用途 | 必需 |
|--------|------|------|
| `ALIBABA_API_KEY` | Claude Code Review 的阿里云百炼 API 密钥 | 是（代码审查） |
| `NUGET_API_KEY` | 发布 NuGet 包到 nuget.org | 否（跳过即不发版到 NuGet） |

---

## 常见问题

**Q: CI 不在 PR 中触发？**
A: 检查：目标分支是否为 `dev` 或 `main`；workflow 文件是否在该分支存在；trigger 条件是否匹配。

**Q: 覆盖率低于 80% 怎么办？**
A: 测试中 `/p:ThresholdLine=80` 会直接使构建失败。需要补充测试用例覆盖未达标的代码路径。

**Q: 想预览发版效果？**
A: 向 `test/*` 分支 push 代码可触发 `pr-build.yml` 的构建和测试，但不触发发版流程。
