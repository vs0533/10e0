# .github/ — GitHub Actions 自动化

## Workflows

### `pr-build.yml` — PR 构建测试

- **触发**: PR 到 `dev` 或 `main`，以及 push 到 `test/**` 分支
- **流程**: restore → build (Release) → **check code format (dotnet format --verify-no-changes)** → test + 覆盖率（门禁 `/p:ThresholdLine=80`）
- **格式化验证**: `dotnet format --verify-no-changes` 阻断 PR（格式化不符的代码无法合并）
- **并发**: 同 PR 旧运行自动取消
- **产物**: test results (.trx/.html) + coverage (.cobertura.xml)
- **权限**: 最小化 (`contents: read`)

### `claude-review.yml` — 自动代码审查

- **触发**: PR opened / synchronize
- **方式**: 安装 `@anthropic-ai/claude-code` CLI，通过 `claude -p` headless 模式审查 diff
- **后端**: 阿里云百炼 API (Qwen 3.7-max)，非 Anthropic 官方
- **超时**: 30 分钟
- **输出**: 作为 PR comment 发布，分 🔴 Critical / 🟡 Suggestion / 🟢 Nit

关键环境变量（与本机 `~/.claude/settings.json` 一致）:
```
ANTHROPIC_AUTH_TOKEN = secrets.ALIBABA_API_KEY
ANTHROPIC_BASE_URL = https://token-plan.cn-beijing.maas.aliyuncs.com/apps/anthropic
ANTHROPIC_MODEL    = qwen3.7-max
```

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
