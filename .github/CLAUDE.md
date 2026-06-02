# .github/ — GitHub Actions 自动化

## Workflows

### `pr-build.yml` — PR 构建测试

- **触发**: PR 到 `dev` 或 `main`
- **流程**: restore → build (Release) → test + 覆盖率 (coverlet)
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

## 修改注意事项

- 修改 workflow 后推送 PR 即可验证，不需要合并到 main
- `release.yml` 只在 push 到 main 时触发，PR 中不会运行
- Claude Review 的 `continue-on-error: true` 确保 API 故障不阻塞 PR 合并
