---
name: triage-loop
description: 自动循环处理仓库的 issues 和 PRs。每一轮从 issue-prioritizer agent 拉取当前最高优先级的未处理项，派发给 process-item 子工作流走完整处理流程（详见正文流程图与 PR #29/#32 schema 化要点），循环往复。完全自主运行（无人值守）。触发关键词："批量处理 issue"、"批量处理 PR"、"sprint 清理"、"triage 循环"、"自动清理"、"处理所有未处理项"、"把 PR 和 issue 都过一遍"、"triage backlog"、"drain queue"。
---

# Triage Loop / 循环分诊

> **执行入口**：`.claude/workflows/triage-loop.js`（外层 while 循环）
> **子工作流**：`.claude/workflows/process-item.js`（单 issue/PR 完整 9 步）
> **遗留文件**：`.claude/workflows/wait-for-pr-review.js`（PR #32 起不再被 process-item 调用，逻辑已 inline 进 agent；保留供参考，可手动调用）
>
> 本文件**只描述规则与派单策略**；具体执行逻辑请读 workflow 脚本注释。
> 用户在 Claude Code 中调用 `Workflow({ name: "triage-loop", args: { ... } })` 触发。

## 何时使用

| 场景 | 用 |
|---|---|
| 想批量清空 issue/PR 积压 | ✅ |
| 长时间挂机清理（人去做别的事） | ✅ |
| 每周 sprint 收尾 | ✅ |
| 只处理单条 issue/PR | ❌ 直接调 `issue-prioritizer` |
| 需要立即得到结果 | ❌ 用前台调用 |
| PR 仅需审查反馈 | ❌ 用 `code-review` skill |
| 修改会影响生产数据 | ❌ 需人在回路 |

## 用法

```bash
# 在 Claude Code 中调用 Workflow 工具
Workflow({ name: "triage-loop", args: { max: 10 } })
Workflow({ name: "triage-loop", args: { max: 20, "issues-only": true } })
Workflow({ name: "triage-loop", args: { max: 5, labels: "bug,p1" } })
Workflow({ name: "triage-loop", args: { "dry-run": true } })

# 子工作流可单独调用（处理单个 issue）
Workflow({ name: "process-item", args: { item: { id: 42, type: "issue", ... } } })

# 等 PR review（手动触发或测试用）
Workflow({ name: "wait-for-pr-review", args: { prNumber: 7, prUrl: "...", timeoutMs: 600000 } })
```

支持的 args（透传给 `triage-loop.js`）：

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `max` | int | 3 | 最大循环轮数；每轮处理 1 个 item |
| `issues-only` | bool | false | 只处理 issue |
| `prs-only` | bool | false | 只处理 PR |
| `labels` | string | — | 逗号分隔，只处理带这些 label 的项 |
| `dry-run` | bool | false | 仅显示将处理什么，不实际派单 |
| `reviewTimeoutMs` | int | 900000 | process-item 盯 PR review 的超时（15 分钟） |

## 派单策略（dispatchKind）

`process-item.js` 内 `dispatchKind(item)` 根据 label + 标题决定走哪条路径：

| labels / 标题特征 | kind | 走的步骤 |
|---|---|---|
| `stale` | `stale` | 只让 `issue-prioritizer` 加 stale label / 评论（不开发） |
| `failing-ci` / `ci-failing` | `fix-ci` | 跳 BDD，走 TDD+tests+review+PR+盯+handle |
| `review-feedback` / `changes-requested` | `fix-review` | 跳 BDD，走 TDD+tests+review+PR+盯+handle |
| `bug`/`p0`/`p1`/`critical` 或标题含 fix/bug/error/exception/crash | `bug` | **完整 9 步**：BranchCheck→BDD→TDD→Tests→Review→Open PR→Watch→Handle |
| `feature` / `enhancement` | `feature` | 完整 9 步 + 前置 planner |
| `refactor` / `tech-debt` / `dead-code` | `refactor` | 跳 BDD+planner，走 TDD+tests+review+PR+盯+handle |
| `docs` / `documentation` | `docs` | 跳 BDD，让 doc-updater 写 |
| 其它 | `default` | 走 general-purpose 兜底 |

**优先级**（issue-prioritizer 输出已遵守）：`bug` > `failing-ci` > `review-feedback` > `feature` > `refactor` > `docs` > `stale`

## 单任务完整工作流（process-item.js）

每个 item 必走（kind 决定哪些步跳过）：

```
1. BranchCheck general-purpose     schema 化 preflight：currentBranch / isFeature / worktreeClean
2. BDD         bdd-guide           写 Given/When/Then 验收测试，必须 RED
3. Plan (feature)  planner          写 plan markdown（仅 feature）
4. TDD         tdd-guide           实现让所有测试 GREEN
5. Tests       general-purpose     schema 化报数字（buildOk / testsOk / failed / passed / formatOk）
6. Review      code-reviewer       本地扫 diff，处理 CRITICAL/HIGH
7. Open PR     general-purpose     推 dev，mcp__github__create_pull_request
8. Watch       general-purpose     inline agent 轮询 claude-review.yml（reviewTimeoutMs 默认 15 分钟）
9. Handle      general-purpose     分类 review：能修修，不能修开 followup issue
```

### schema 化要点（PR #32 修复 w7bu0omg2 案例）

- **BranchCheck**：`BRANCHCHECK_SCHEMA` 强制 `currentBranch` 是非空字符串，agent 不许省略 / 写 undefined
- **Tests**：`TESTS_SCHEMA` 要求 `buildOk` / `testsOk` / `formatOk` boolean + `failed` / `passed` / `skipped` number。process-item 直接看字段，**不依赖正则**
- **Watch**：原 `workflow('wait-for-pr-review', ...)` 因 harness 单层嵌套限制被废弃，改用 inline agent 轮询（schema 化 `REVIEW_SCHEMA`）
- **dotnet 命令必须带 `DOTNET_CLI_UI_LANGUAGE=en-US`**：zh_CN locale 下 CLI 输出中文 "已通过!"，`Passed!` 匹配不到 → 误判失败（PR #29 修复）

### 8→9 步的 review 分类原则

| 类型 | 处理 | 例子 |
|---|---|---|
| 拼写 / typo / 命名 | 修 | 变量名拼错 |
| 局部重构 / 提取方法 | 修 | 重复代码、过长函数 |
| 注释 / 文档补充 | 修 | 缺 XML doc |
| 测试覆盖不足 | 补测试再 push | 漏边界 case |
| **架构 / 大重构** | **开新 issue** | "建议拆分为 X 模块" |
| **新功能建议** | **开新 issue** | "顺便加个 Y 能力" |
| **跨 PR 范围** | **开新 issue** | 改到别的子系统 |
| **设计争论** | **开新 issue + `discussion`** | 风格争论 |

不能在本 PR 处理的，统一 issue 格式：

```markdown
# 标题
Followup from #<pr-number>: <review 反馈摘要>

## 来源
- PR: #<pr-number>
- Review: <链接到具体 review comment>
- 反馈人: <用户名>

## 背景
<复述 review 反馈>

## 为什么不在本 PR 处理
<范围 / 风险 / 时间>

## 建议处理
- 标签: `followup-from:#<pr-number>`, `enhancement` 或 `refactor`
- 优先级: 由下一轮 issue-prioritizer 排
```

## worktree 隔离策略

按"外层 worktree，内层接力"：

- **triage-loop.js**：while 循环本身**不带** `isolation: "worktree"`，让 process-item 自管。
- **process-item.js**：第 1 步 BDD / TDD agent **不带** `isolation: "worktree"`，全部 9 步在同一 workspace 接力（顺序执行，状态自动可见）。
- **wai-for-pr-review.js**：轮询 agent **不带** `isolation`，只读 GitHub API 不改文件。
- **跨 item 隔离**：靠 git 分支（每 item 推到独立 feature 分支再开 PR），不靠 worktree。

> **不要给 BDD / TDD 加 `isolation: "worktree"`**：新机制下每次 `isolation: "worktree"` 都开**全新** worktree，BDD 写的测试 TDD 看不到。

## 已知陷阱（必读）

- **worktree 隔离是给并行用的**：本工作流顺序执行，9 步接力同一 workspace，不要每步 isolation。
- **GitHub API 限流**：未认证 `60/h`，认证 `5000/h`。先 `gh auth login`。
- **不要自动 merge PR / close issue**：所有改动让用户确认。
- **单 item > 30 分钟应拆**：`reviewTimeoutMs` 调到 1800000 但要警惕 budget；超长拆子任务。
- **`--dry-run` 必走一遍**：实际派单前先 dry-run 确认队列内容。
- **issue-prioritizer 会同时返回 PR 和 issue**：用 `issues-only` / `prs-only` 过滤。
- **review 反馈必须分类**：能修在本 PR 就修，不能修的开新 issue（标 `followup-from:#<pr>`），不要全收。
- **planner / bdd-guide / tdd-guide 拆三步**：feature 类型依次走完三步（占 3 轮子派单），不是 1 轮。
- **linter 看不到顶层 return 是 consumer**：return 前用 `log()` 显式打印关键变量，避 `noUnused` 警告。
- **workflow 脚本无 fs / Date / Math.random**：需要持久化或时间戳就传到 agent 内部。

## 故障排查

| 症状 | 原因 | 修复 |
|---|---|---|
| 队列始终不空 | issue-prioritizer 把已派单也返回 | issue-prioritizer prompt 加 `!has linked pr` 过滤 |
| BDD 写的测试 TDD 看不到 | 给 BDD 加了 `isolation: "worktree"` | 删掉 isolation，让 TDD 接力 |
| PR review 等不到 | claude-review.yml 没跑 / secret 缺失 | 检查 `ALIBABA_API_KEY`；改短 `reviewTimeoutMs` |
| 子 agent 返回 schema 不匹配 | prompt 没强调字段 | schema 必填字段在 prompt 重复一次 |
| 循环第 1 项就卡住 | agent prompt 缺上下文 | 至少传 `id, url, title, body` |
| max 太小跑不完 | feature 走 BDD+planner+TDD+PR 实际 3-5 轮 | `max: 20+` |
| 同一 PR 被派单多次 | issue-prioritizer 未排除已派 | filter 加 `!assigned` 或 `!linked_pr` |
| followup issue 又被新 triage 拉回 | 标签缺失 | 标 `followup-from:#<pr>` 让 issue-prioritizer 降优先级 |
| 顶层 return 后 linter 报 `noUnused` | linter 不认 workflow 顶层 return | 在 return 前 `log()` 显式用变量 |

## 相关资源

- **Workflow 脚本**：
  - `.claude/workflows/triage-loop.js`（外层循环）
  - `.claude/workflows/process-item.js`（单 item 9 步）
  - `.claude/workflows/wait-for-pr-review.js`（盯 PR review）
  - `.claude/workflows/triage-loop-test.js`（烟雾测试，验证 schema 不破）
  - `.claude/workflows/lib/dispatch.js`（派单策略共享模块）
- **Slash 命令**：
  - `/triage`（`.claude/commands/triage.md`）— 包一层命令，解析 `--max/--issues-only/--labels/--dry-run` 后调 `Workflow({ name: "triage-loop", ... })`
  - `/triage-loop-test` — 跑烟雾测试（也可直接 `Workflow({ name: "triage-loop-test" })`）
- **子 agent**：`issue-prioritizer`、`bdd-guide`、`tdd-guide`、`planner`、`code-reviewer`、`refactor-cleaner`、`build-error-resolver`、`doc-updater`、`security-reviewer`、`general-purpose`
- **GitHub MCP**：`mcp__github__*` 工具族
  - `get_pull_request_reviews` / `get_pull_request_comments`：拉 review
  - `create_issue`：开 followup issue
  - `create_pull_request`：开 PR
- **CI**：`.github/workflows/claude-review.yml`（PR 自动 review, Qwen headless）
- **项目级**：`/Users/wilder/dev/10e0/CLAUDE.md`（开发规范）、`/agents/issue-prioritizer.md`

## 调优建议

| 场景 | 建议 |
|---|---|
| 首次使用 | `dryRun: true` → `max: 3` 试跑 → 看输出再调 |
| 积压严重（> 50 项） | `max: 50`，跑 1-2 小时看效果 |
| 只想清 stale | `labels: "stale"` + `max: 5` |
| CI 经常失败 | issue-prioritizer 把 `failing-ci` 排前 |
| API 经常 429 | `max: 10` + 每轮之间 sleep 30s（脚本可加） |
| review 等待太久 | `reviewTimeoutMs: 600000`（10 分钟） |
