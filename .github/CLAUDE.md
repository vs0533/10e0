# .github/ — GitHub Actions 自动化

## Workflows

### `pr-build.yml` — PR 构建测试

- **触发**: PR 到 `dev` 或 `main`，以及 push 到 `test/**` 分支
- **流程**: restore → build (Release) → **check code format (dotnet format --verify-no-changes)** → test + 覆盖率（门禁 `/p:ThresholdLine=80`）
- **测试过滤**: `--filter "Requires!=Docker"` — 跳过 Docker 依赖测试（见 `docker-integration-tests.yml`）
- **格式化验证**: `dotnet format --verify-no-changes` 阻断 PR（格式化不符的代码无法合并）
- **并发**: 同 PR 旧运行自动取消
- **产物**: test results (.trx/.html) + coverage (.cobertura.xml)
- **权限**: 最小化 (`contents: read`)

### `docker-integration-tests.yml` — Docker 集成测试

- **触发**: PR 到 `dev` / `main`（与 pr-build 同步）
- **流程**: restore → build (Release) → `dotnet test --filter "Requires=Docker"`
- **范围**: 仅跑 `[Trait("Requires", "Docker")]` 的测试 —— 目前是 `OutboxRelayConcurrencyTests`（feature #82）：
  - 用 `Testcontainers.MsSql` 启真实 SQL Server 2022 容器
  - 两个独立 `IServiceProvider` + 共享 L2 缓存 = 真分布式锁 SETNX / 续约验证
  - 30 轮 × 50 条消息并发跑，断言 Publisher 每条消息恰好被调 1 次
- **Runner**: `ubuntu-latest`（自带 Docker daemon），`timeout-minutes: 30`
- **与 pr-build 关系**: pr-build 跳过这些测试（Testcontainers 慢、CI 资源消耗大），本 workflow 单独跑并要求绿
- **本地等价**: `dotnet test`（需 Docker Desktop 开着）

### `claude-review.yml` — 自动代码审查

- **触发**: PR opened / synchronize
- **方式**: 安装 `@anthropic-ai/claude-code` CLI，通过 `claude -p` headless 模式审查 diff
- **后端**: MiniMax API (MiniMax-M3)，非 Anthropic 官方
- **超时**: 30 分钟
- **输出**: 末尾强制输出 `VERDICT: APPROVE`（无 🔴 Critical）或 `VERDICT: REQUEST_CHANGES`（有 🔴 Critical / 解析失败保守拒绝）；分 🔴 Critical / 🟡 Suggestion / 🟢 Nit
- **发布方式**: 有 `REVIEW_BOT_TOKEN` (PAT) 且非 self-approve 时用 `pulls.createReview` 发**正式 review**（计入 branch protection approval）；否则降级为 issue comment（**不计入 approval**）

关键环境变量:
```
ANTHROPIC_AUTH_TOKEN = secrets.MINIMAX_API_KEY
ANTHROPIC_BASE_URL   = https://api.minimaxi.com/anthropic
ANTHROPIC_MODEL      = MiniMax-M3
```

> **triage 消费方契约（厂商无关）**：`.claude/workflows/process-item.js` 靠两个**与模型无关**的稳定标识认本 bot 评论并解析结论：
> ① HTML marker `<!-- triage-review-bot -->`（+ `## 🤖 Automated Code Review` header）定位是 bot 评论；
> ② `Verdict: **APPROVE|REQUEST_CHANGES**` 行作最终结论。
> **换 review 后端模型只改本文件的 `ANTHROPIC_MODEL` 等 env 即可**——marker / Verdict 格式不变，triage 侧零改动。
> 反之，**若改了 marker 或 VERDICT 输出格式，必须同步 process-item.js 的 Watch Review 解析逻辑**。

### `release.yml` — 自动发版

- **触发**: push 到 `main`（即 PR 合并）
- **流程**: build → test → 计算 SemVer → dotnet pack → git tag → GitHub Release → 可选 NuGet 发布
- **并发**: `cancel-in-progress: false`（防止发版中途取消）
- **tag 推送**: 使用 `GITHUB_TOKEN`，不会重复触发 push 事件
- **NuGet 发布**: 仅在 `NUGET_API_KEY` secret 存在时执行

## CodeQL 安全扫描

- **默认 Setup**: 仓库通过 Settings → Code security 启用 **default setup**，GitHub 自动对 `csharp` 和 `actions` 进行 CodeQL 分析
- **高级 Workflow 已移除**: PR #6 曾尝试添加 advanced workflow，因与 default setup 冲突而撤销（"CodeQL analyses from advanced configurations cannot be processed when the default setup is enabled"）
- 无需手动配置 workflow 文件，GitHub 自动管理和运行扫描
- **项目 workflow 必须 grant `security-events: write`**（PR #33 教训）：GitHub 会 auto-inject "Perform CodeQL Analysis" step 到**所有**项目拥有的 workflow（不只 default setup），该 step 需要此权限才能 upload SARIF 到 Security tab。缺它会报：
  ```
  ##[error]Please check that your token is valid and has the required permissions:
          contents: read, security-events: write
  ```
  症状表现为 Code Review job 偶发失败、rerun 偶尔能过——根因是 GITHUB_TOKEN 临时刷新掩盖了权限缺失。`claude-review.yml` 已加该权限，后续新增 workflow 必须在 `permissions:` 段同步加上

## 修改注意事项

- 修改 workflow 后推送 PR 即可验证，不需要合并到 main
- `release.yml` 只在 push 到 main 时触发，PR 中不会运行
- Claude Review 的 `continue-on-error: true` 确保 API 故障不阻塞 PR 合并

### `dependabot.yml` — 依赖自动更新

- **触发**: 每周（schedule: weekly）
- **包管理器**: NuGet + GitHub Actions
- **分组策略**:
  - `Microsoft.*` — Microsoft 系包自成一组（减少 PR 噪音）
  - `AWSSDK.*` — AWS SDK 包自成一组
  - 其他依赖按默认策略（各自独立）
- **标签**: `dependencies`
- **目标分支**: `dev`
- **行为**: 自动创建 PR，到期未合并则自动关闭
