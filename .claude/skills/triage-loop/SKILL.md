---
name: triage-loop
description: 自动循环处理仓库的 issues 和 PRs。每一轮从 issue-prioritizer agent 拉取当前最高优先级的未处理项，派发给 process-item 子工作流走完整处理流程（详见正文流程图与 PR #29/#32 schema 化要点），循环往复。完全自主运行（无人值守）。触发关键词："批量处理 issue"、"批量处理 PR"、"sprint 清理"、"triage 循环"、"自动清理"、"处理所有未处理项"、"把 PR 和 issue 都过一遍"、"triage backlog"、"drain queue"。
---

# Triage Loop / 循环分诊

> **执行入口**：`.claude/workflows/triage-loop.js`（外层 while 循环）
> **子工作流**：`.claude/workflows/process-item.js`（单 issue/PR 完整 9 步）
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

# 盯 PR review 的轮询逻辑已 inline 在 process-item 的 Watch Review 阶段
# （旧 wait-for-pr-review.js 因 harness 单层嵌套限制已删除，不再单独调用）
```

支持的 args（透传给 `triage-loop.js`）：

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `max` | int | 3 | 最大循环轮数；每轮处理 1 个 item |
| `issues-only` | bool | false | 只处理 issue |
| `prs-only` | bool | false | 只处理 PR |
| `labels` | string | — | 逗号分隔，只处理带这些 label 的项 |
| `dry-run` | bool | false | 仅显示将处理什么，不实际派单 |
| `reviewTimeoutMs` | int | 900000 | process-item 每轮等 CI/review 的超时（15 分钟） |
| `maxReviewRounds` | int | 3 | process-item review-fix 循环最多重试轮数（REQUEST_CHANGES→修+push→重等 的上限） |
| `continueOnUnmerged` | bool | false | 默认 item 没成功合并到 dev 就停止整个循环（必须合并+同步 dev 才跑下一个）；开此项则未合并/失败时跳过该项继续 |

## 派单策略（dispatchKind）

`process-item.js` 内 `dispatchKind(item)` 根据 label + 标题决定走哪条路径：

| labels / 标题特征 | kind | 走的步骤 |
|---|---|---|
| `stale` | `stale` | 只让 `issue-prioritizer` 加 stale label / 评论（不开发） |
| `failing-ci` / `ci-failing` | `fix-ci` | 跳 BDD，走 TDD+tests+review+PR+盯+handle |
| `review-feedback` / `changes-requested` | `fix-review` | 跳 BDD，走 TDD+tests+review+PR+盯+handle |
| `bug`/`critical` 或标题含 fix/bug/error/exception/crash | `bug` | **完整流程**：BranchCheck→BDD→TDD(×3)→Tests→Review→Open PR→Watch→Handle→Merge |
| `feature` / `enhancement` | `feature` | 完整流程 + 前置 planner |
| `refactor` / `tech-debt` / `dead-code` | `refactor` | 跳 BDD+planner，走 TDD+tests+review+PR+盯+handle |
| `docs` / `documentation` | `docs` | 跳 BDD，让 doc-updater 写 |
| 其它 | `default` | 走 general-purpose 兜底 |

**优先级**（issue-prioritizer 输出已遵守）：`bug` > `failing-ci` > `review-feedback` > `feature` > `refactor` > `docs` > `stale`

## 单任务完整工作流（process-item.js）

每个 item 必走（kind 决定哪些步跳过）：

```
1.  BranchCheck  general-purpose   schema 化 preflight：强制 `git checkout -f dev` + pull，再切干净 feature 分支
2.  BDD          bdd-guide         写 Given/When/Then 验收测试，必须 RED
3.  Planner      planner           写 plan markdown（仅 feature）
4.  TDD-Schema   tdd-guide         DB/接口/DI，< 5 文件
5.  TDD-Impl     tdd-guide         handler/service 实现，让测试 GREEN
6.  TDD-Verify   general-purpose   build/test/format 兜底验证
7.  Tests        general-purpose   schema 化报数字（buildOk / testsOk / failed / passed / formatOk）
8.  Local Review code-reviewer     本地扫 diff，处理 CRITICAL/HIGH
9.  Open PR      general-purpose   push feature 分支，mcp__github__create_pull_request（base=dev）
10-12. review-fix 循环（最多 `maxReviewRounds`=3 轮，Watch/Handle/Merge 反复跑）：
        每轮 Watch Review 等 CI + 拉三类评论 + 解析 bot VERDICT →
        - **APPROVE** → Merge & Sync：CI 绿 + mergeable 时 squash 合并到 dev + 同步本地，结束
        - **REQUEST_CHANGES** → Handle Review：能在本 PR 修的修 + `git push`（触发新 CI+新 review）→ **下一轮重等**；
          不能修的开可追溯 followup issue。本轮没 push 任何修复 → 停（留人工，issue 已追溯）
        - **NONE**（bot 没产出 VERDICT）→ 不合并，留人工
```

> **收紧门禁（合并模式）**：**只有 bot 明确 `VERDICT: APPROVE` 才自动合并**；`REQUEST_CHANGES` / `NONE` 一律不自动合。
> 三层叠加：① bot VERDICT=APPROVE（脚本层确定性判断）→ ② CI(`pr-build.yml`) 绿 + mergeable → ③ branch protection（422 则标记 reason 跳过，不崩溃）。
>
> **REQUEST_CHANGES 的自愈循环**：能在本 PR 解决的问题，Handle 当场改 + push，靠 `synchronize` 触发新一轮 CI+review，
> 循环直到 APPROVE 或耗尽 `maxReviewRounds`；不能在本 PR 解决的开 `followup-from:#<pr>` issue（含来源 PR/review 链接/原因）保证可追溯。
> 判断「要不要再转一轮」靠 Handle 回报的 `pushed`/`fixedCount`——**本轮没 push 新代码就别空等**（CI/verdict 结果不会变）。
>
> 🛑 **冲突门禁（每轮先查）**：Watch Review 拉 `mergeable`/`mergeStateStatus`，若 `CONFLICTING`/`DIRTY` → **立即停自愈循环留人工**，
> 不空转——自愈循环能修 bot review 意见，但修不了 merge 冲突。常见根因：重复处理了已被合并 PR 解决的 issue，或 base 落后需 rebase。
>
> 🔒 **防重复处理**：Open PR 阶段，issue 类型的 PR 正文**必须**写 `Closes #<issue.id>`，合并后自动关 issue；
> 否则 issue 遗留 open，下轮 triage 会把它当 open issue 重新拉起、重做一遍，与已合并改动冲突（#51→#55→#58 真实教训）。
>
> ⚠️ **claude-review 必须拉三类评论**：bot 可能发正式 review（`gh pr view --json reviews`）、行内 comment
> （`pulls/N/comments`），也可能降级成 issue comment（`issues/N/comments`）——只拉前两类会漏掉 fallback 评论，解析不到 VERDICT。

### schema 化要点（PR #32 修复 w7bu0omg2 案例）

- **BranchCheck**：`BRANCHCHECK_SCHEMA` 强制 `featureBranch` 是非空字符串；preflight 一律先回干净 dev 再切分支
- **Tests**：`TESTS_SCHEMA` 要求 `buildOk` / `testsOk` / `formatOk` boolean + `failed` / `passed` / `skipped` number。process-item 直接看字段，**不依赖正则**
- **Watch**：原外部子工作流调用（`workflow('wait-for-pr-review', ...)`）因 harness 单层嵌套限制被废弃，改用 inline agent 轮询（schema 化 `REVIEW_SCHEMA`）
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

按"不隔离，git 分支兜底"：

- **triage-loop.js**：while 循环**不带** `isolation: "worktree"`。
- **process-item.js**：调用方（triage-loop）**不带** isolation，所有阶段在同一 workspace（用户主仓库工作目录）顺序接力（状态自动可见）。
- **轮询 agent（Watch Review）**：只读 GitHub API 不改文件，**不带** isolation。
- **跨 item 隔离**：靠 git 分支——**每个 item 在 BranchCheck 阶段强制 `git checkout -f dev` + pull，再 `git checkout -B feature/<id>-<slug>`**，保证每个 PR 基于干净最新 dev、互不污染。**这是修 Bug A（分支基线污染）的关键。**

> **不要给 BDD / TDD 加 `isolation: "worktree"`**：每次 `isolation: "worktree"` 都开**全新** worktree，BDD 写的测试 TDD 看不到。
> **不要让 process-item 处理完停在 feature 分支不回 dev**：会导致下一个 item 的分支基于上一个 item 的分支（基线污染）。BranchCheck 已强制每轮回 dev 解决，但若手动改流程务必保持这一点。

## 已知陷阱（必读）

- **worktree 隔离是给并行用的**：本工作流顺序执行，全部阶段接力同一 workspace，不要每步 isolation。
- **GitHub API 限流**：未认证 `60/h`，认证 `5000/h`。先 `gh auth login`。
- **全自动合并模式**：Merge & Sync 阶段会在 CI 绿+mergeable 时自动 squash 合并到 dev 并同步本地 dev。**dev→main 合并不在此流程内**（仍由人工触发发版）。若不想自动合并，把 process-item 的 Merge & Sync 阶段改成只 log 不 merge。
- **branch protection 会拦自动合并**：dev 要求 ≥1 review approval 时，merge API 返回 422，Merge & Sync 标记 `reason="需人工 approve"` 跳过，不崩溃——此时 PR 留在 open 等人工合并。
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
| 同一 PR 被派单多次 | issue-prioritizer 未排除已派 | triage-loop 已有 `seen` 会话内去重兜底；根治仍建议 issue-prioritizer filter 加 `!linked_pr` |
| followup issue 又被新 triage 拉回 | 标签缺失 | 标 `followup-from:#<pr>` 让 issue-prioritizer 降优先级 |
| 顶层 return 后 linter 报 `noUnused` | linter 不认 workflow 顶层 return | 在 return 前 `log()` 显式用变量 |
| 第 2 个 item 起 PR diff 含上一个 PR 的改动 | 处理完没回 dev，新分支基于旧 feature 分支（Bug A） | 已修：BranchCheck 每轮强制 `git checkout -f dev` + pull 再切分支 |
| 某 item 失败后整个 loop 崩溃 | process-item throw 未被捕获（Bug C） | 已修：triage-loop 用 try/catch 包 workflow 调用 + process-item 主体 try/catch 返回 `{ok:false}` |
| 失败 item 留下脏工作区，后续每轮都卡 | 旧 BranchCheck「脏即 throw」（Bug E） | 已修：BranchCheck 用 `git checkout -f dev` 丢弃 feature 分支残留（dev/main 脏时仍 throw 保护用户改动） |
| 自动合并不生效，PR 停在 open | branch protection 要求人工 approve，merge API 422 | 预期行为：Merge & Sync 标 `reason="需人工 approve"` 跳过，需人工合并 |
| `--dry-run` 重复打印同一项 N 次 | 旧版 dry-run 进 while 循环 rank N 次（Bug D） | 已修：dry-run 只 rank 一次列出整个队列后 return |
| Watch Review 阶段 CI 早绿却空转卡死 >40min | 旧版让 LLM 自己数 sleep 次数轮询，MiniMax-M3 不可靠（Bug H） | 已修：Watch/Merge 改用确定性 `for i in $(seq 1 N)` shell 循环控制次数，LLM 只执行命令不数数 |
| item 代码全写完、测试全过，只因缺末尾换行被整个跳过 | TDD-Verify/Tests 的 `dotnet format` 是 `--verify-no-changes` 只验证不修复（小模型必漏换行） | 已修：format 改「先 `dotnet format` 自动修复，再 `--verify-no-changes` 确认」（#49 案例：751 测试全过只卡 formatOk=false） |
| 失败 item 的改动下一轮被 `checkout -f dev` 丢光 | catch 块没保存就交给 BranchCheck 强切 | 已修：catch 块先 `git add -A && commit && push` WIP 到 feature 分支保住改动，返回 `savedBranch` |
| `--max 1` 却看到 process-item 跑「两轮」 | UI 显示 process-item 内部 8 个子 agent 阶段，非真重复派单（运行记录 processed=0,skipped=1） | 非 bug，是 UI 显示内部阶段；真要「未合并不继续」用默认 stopOnUnmerged 行为 |
| 同一 issue 被重做、PR 与 dev 冲突、自愈循环空转 | 已合并 PR 没写 `Closes #N` → issue 遗留 open 被下轮重复处理（#51→#55→#58） | 已修：Open PR 对 issue 强制写 `Closes #<id>`；自愈循环每轮先查 `mergeable`，`CONFLICTING/DIRTY` 立即停留人工 |

## 相关资源

- **Workflow 脚本**（均自包含，无 relative import）：
  - `.claude/workflows/triage-loop.js`（外层循环：rank → 过滤 → 派单；含会话内去重 + 单项失败兜底）
  - `.claude/workflows/process-item.js`（单 item 12 阶段；`dispatchKind` 派单策略已内联到文件底部）
- **Slash 命令**：
  - `/triage`（`.claude/commands/triage.md`）— 包一层命令，解析 `--max/--issues-only/--prs-only/--labels/--dry-run` 后调 `Workflow({ name: "triage-loop", ... })`
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
