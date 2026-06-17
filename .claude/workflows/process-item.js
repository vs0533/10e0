// 单 issue/PR 完整工作流（meta.phases 列表是事实源）
//   BranchCheck → Dispatch → [Planner] → BDD → TDD-Schema → TDD-Impl → TDD-Verify
//   → Tests → Local Review → Open PR → Watch Review → Handle Review → Merge & Sync
// 派单策略（dispatchKind）已内联到本文件底部（harness 不支持 relative import）
//
// worktree 策略（与 SKILL.md 一致）：
//   triage-loop.js 调本工作流时**不带** isolation——本工作流顺序执行，所有阶段
//   接力同一 workspace（即用户主仓库工作目录），靠 git 分支做跨 item 隔离。
//   因此每个 item 必须由 BranchCheck 强制回到干净的最新 dev 后再切 feature 分支，
//   否则会发生「第 N 个 item 的分支基于第 N-1 个 item 分支」的基线污染。
//
// 输入：args.item = { id, type, url, title, body, labels, priority }
// 输出：{ ok, prNumber, prUrl, merged, followupCount, error? }
//   失败：返回 { ok:false, error, stage } —— 让 triage-loop 跳过本项继续，而不是崩溃整个循环。

export const meta = {
  name: 'process-item',
  description: '单 issue/PR 走完整流程：BranchCheck→BDD→TDD→tests→review→PR→盯 review→分类→合并到 dev+同步本地',
  phases: [
    { title: 'BranchCheck' },
    { title: 'Dispatch' },
    { title: 'Planner' },
    { title: 'BDD' },
    { title: 'TDD-Schema' },
    { title: 'TDD-Impl' },
    { title: 'TDD-Verify' },
    { title: 'Tests' },
    { title: 'Local Review' },
    { title: 'Open PR' },
    { title: 'Watch Review' },
    { title: 'Handle Review' },
    { title: 'Merge & Sync' },
  ],
}

// —— 内联自旧 lib/dispatch.js（harness 工作流发现机制不支持 relative import，
//    所有工作流文件必须自包含）
// 注意：BUG_LABELS 故意不含 p0/p1——优先级标签不代表 bug 类型
// issue #7 案例：label = [enhancement, p1, security] 命中 'feature' 走完整流程 + planner
// 之前 BUG_LABELS 含 p1 导致 enhancement + p1 被误判为 bug，跳过 planner 后 TDD 撑爆
const STALE_LABELS = ['stale']
const FAILING_CI_LABELS = ['failing-ci', 'ci-failing']
const REVIEW_FEEDBACK_LABELS = ['review-feedback', 'changes-requested']
const BUG_LABELS = ['bug', 'critical']
const FEATURE_LABELS = ['feature', 'enhancement']
const REFACTOR_LABELS = ['refactor', 'tech-debt', 'dead-code']
const DOCS_LABELS = ['docs', 'documentation']
const BUG_TITLE_REGEX = /fix|bug|error|exception|crash/i

function dispatchKind(item) {
  const labels = item.labels || []
  if (labels.some(l => STALE_LABELS.includes(l))) return 'stale'
  if (labels.some(l => FAILING_CI_LABELS.includes(l))) return 'fix-ci'
  if (labels.some(l => REVIEW_FEEDBACK_LABELS.includes(l))) return 'fix-review'
  // feature 优先于 bug：标题里有 fix/bug 但 labels 含 feature/enhancement 时按 feature 走（带 planner）
  if (labels.some(l => FEATURE_LABELS.includes(l))) return 'feature'
  if (labels.some(l => BUG_LABELS.includes(l)) || BUG_TITLE_REGEX.test(item.title || '')) return 'bug'
  if (labels.some(l => REFACTOR_LABELS.includes(l))) return 'refactor'
  if (labels.some(l => DOCS_LABELS.includes(l))) return 'docs'
  return 'default'
}
const SKIP_BDD_KINDS = new Set(['refactor', 'docs', 'fix-ci', 'fix-review', 'stale', 'default'])
const NEEDS_PLANNER_KINDS = new Set(['feature'])
// —— 内联结束

const item = args.item
if (!item) throw new Error('process-item: args.item 必填')

// 在主仓库工作目录唯一生成 feature 分支名（BranchCheck 与 Merge & Sync 共用同一个名字，
// 避免「创建模板」与「识别正则」两处规则不一致的历史 bug）
const featureBranch = `feature/${item.id}-${(item.title || 'fix')
  .toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 40)}`

// —— preflight：强制同步到干净的最新 dev，再从 dev 切出 feature 分支
//
// 修复（2026-06-17）：
//   Bug A 分支基线污染——旧版处理完停在 feature 分支不回 dev，下一轮 BranchCheck
//          复用/基于上一个 item 的分支建新分支，导致第 N 个 PR 含第 N-1 个 PR 的改动。
//   Bug B 命名识别不一致——创建模板是 `feature/<id>-` 但识别正则是 `feature/issue-N-*`，
//          永远匹配不上自己创建的分支。
//   Bug E 脏残留卡死——某 item 中途失败留下脏工作区，后续每轮 BranchCheck 都因「脏」throw。
//   统一修法：不再「识别并复用」当前分支，每个 item 一律先回到最新 dev 再 checkout -b。
//
// 历史：
//   2026-06-17 (wiixtktam) 修「dev 上永远 throw」死锁。
//   此前       (w7bu0omg2)  原版 free-text 回报误报分支，改 schema 强制返回。
const BRANCHCHECK_SCHEMA = {
  type: 'object',
  required: ['featureBranch', 'baseSynced'],
  properties: {
    featureBranch: { type: 'string', minLength: 1 },
    baseSynced: { type: 'boolean' },
    fromBranch: { type: 'string' },
    error: { type: 'string' },
  },
}
const branchCheck = await agent(
  `把工作区同步到干净的最新 dev，然后切出 feature 分支 \`${featureBranch}\`。\n\n` +
  `按顺序执行（任一步 fatal 就把原因原样写进 error 字段返回，**不要猜**）：\n` +
  `1. \`git rev-parse --abbrev-ref HEAD\` 记为 fromBranch；\`git status --porcelain\` 看工作区是否脏\n` +
  `2. 若 fromBranch == \`dev\` 且工作区脏 → error="dev 有未提交改动，请先 commit/stash 再跑 triage"（这是用户的改动，**绝不能丢**）\n` +
  `3. 若 fromBranch == \`main\` 且工作区脏 → error="main 有未提交改动，agentic 不处理"\n` +
  `4. 其余情况（在 dev 干净 / main 干净 / feature/* / 其他分支，可能有上一轮失败残留）：\n` +
  `   - \`git checkout -f dev\` 强制切到 dev（丢弃工作区残留是安全的：成功的 item 已 push，失败的本就该丢）\n` +
  `5. 已在 dev 后：\`git pull --ff-only origin dev\` 同步最新（baseSynced=true；若 pull 失败把原因写 error）\n` +
  `6. 从干净 dev 切出 feature 分支：\`git checkout -B ${featureBranch}\`（-B 保证同名分支存在时也重建到当前 dev）\n` +
  `7. \`git branch --show-current\` 确认，把实际分支名填进 featureBranch\n\n` +
  `**重要**：精确读 git 输出，命令报 fatal error 就原样回报 error。\n` +
  `回报 schema（featureBranch 必须是非空字符串）：{ featureBranch: string, baseSynced: boolean, fromBranch?: string, error?: string }`,
  { phase: 'BranchCheck', agentType: 'general-purpose', schema: BRANCHCHECK_SCHEMA }
)
log(`分支 preflight: ${JSON.stringify(branchCheck)}`)
if (branchCheck.error) throw new Error(`BranchCheck failed: ${branchCheck.error}`)
const workBranch = branchCheck.featureBranch || featureBranch

const kind = dispatchKind(item)
log(`#${item.id} 派单: ${kind} (${item.title})`)

// 整个处理流程包在 try 里：单 item 失败返回 { ok:false } 让 triage-loop 跳过继续，
// 而不是 throw 崩溃整个循环（双保险——triage-loop 侧也有 try/catch）。
try {
  // stale / 简单 issue 不走完整流程
  if (kind === 'stale') {
    await agent(
      `判断 issue #${item.id} (${item.title}) 是否需要关闭或 ping 作者。` +
      `如果超过 30 天无活动且无进展评论，添加 stale label 并评论"如无更新将在 7 天后关闭"。`,
      { phase: 'Dispatch', agentType: 'issue-prioritizer' }
    )
    return { ok: true, prNumber: null, merged: false, followupCount: 0 }
  }

  // 1. BDD（bug + feature 走；refactor/docs/fix-ci/fix-review/stale/default 跳过）
  if (!SKIP_BDD_KINDS.has(kind)) {
    await agent(
      `BDD for #${item.id} "${item.title}"\n\n` +
      `Issue body:\n${item.body}\n\n` +
      `任务：编写 Given/When/Then 格式的验收测试（xUnit + FluentAssertions），` +
      `位置 tests/10E0.{Api,Core}.Tests/ 下，命名 {Feature}AcceptanceTests.cs。` +
      `先写测试，dotnet test 确认 RED。如果不写测试或测试不 RED，本步骤视为失败。`,
      { phase: 'BDD', agentType: 'bdd-guide' }
    )
  }

  // 2. Planner（仅 feature 大改先规划）
  if (NEEDS_PLANNER_KINDS.has(kind)) {
    await agent(
      `为 #${item.id} "${item.title}" 写实现 plan：涉及哪些模块、新增哪些类型、` +
      `对现有 API 的影响、是否需要数据库迁移。产出 plan markdown 不超过 200 行。`,
      { phase: 'Planner', agentType: 'planner' }
    )
  }

  // 3 步 TDD（避免单 agent 撑爆 context）：
  //   1. schema/接口（DB 模型、新接口签名、DI 注册）—— 改动 < 5 文件
  //   2. 实现（handler/service/evaluator 改写）—— 改动 < 5 文件
  //   3. 单元测试补全（覆盖边界条件）+ 跑全 suite
  // 每步独立 agent，靠文件系统接力；如果上一步没产出，本步就少干点
  //
  // 关于 needsDownstream：小 issue（无新 schema/无新接口）Step 1 会返回
  // { needsDownstream: true, reason } —— 这是**正常**流，不是错误。Step 2 接力做完整实现。
  await agent(
    `TDD Step 1/3 (schema & interfaces) for #${item.id} "${item.title}"\n\n` +
    `Issue body:\n${item.body}\n\n` +
    `范围：仅改 DB 模型/实体（加字段/迁移）、新接口/抽象、DI 注册扩展。\n` +
    `**硬约束**：\n` +
    `- 改动 < 5 个文件，超了就 throw 退出，让上层拆 issue\n` +
    `- 不写 handler、不写 service、不写 tests\n` +
    `- 完成后 \`DOTNET_CLI_UI_LANGUAGE=en-US dotnet build 10e0.slnx 2>&1 | tail -10\`，必须通过（已有 RED 测试无所谓）\n` +
    `- 回报：{ filesChanged, buildOk, newInterfaces } 结构化数据\n\n` +
    `如果发现本步需要改 handler/service（说明 issue 拆得不够细），回报 { needsDownstream: true, reason } 并退出。`,
    { phase: 'TDD-Schema', agentType: 'tdd-guide' }
  )

  await agent(
    `TDD Step 2/3 (implementation) for #${item.id} "${item.title}"\n\n` +
    `**前置条件**：Step 1 已完成（schema/接口就位）。用 Read 工具看新接口定义和已有 RED 测试。\n` +
    `范围：让 RED 验收测试和已有所有测试全 GREEN——改 handler/service/evaluator。\n` +
    `**硬约束**：\n` +
    `- 改动 < 5 个文件\n` +
    `- 跑 \`DOTNET_CLI_UI_LANGUAGE=en-US dotnet test 10e0.slnx --nologo 2>&1 | tail -30\` 必须显示 "Failed: 0"（既有测试不许破）\n` +
    `- 写必要的新单元测试覆盖边界（但 < 3 个测试文件）\n` +
    `- 回报：{ filesChanged, testsPass, newUnitTests, remainingRed } 结构化数据\n\n` +
    `如果本步跑超过 30 分钟还没 GREEN，先 commit 当前进度，回报 { stalled: true, partialFiles, remainingFails } 退出。`,
    { phase: 'TDD-Impl', agentType: 'tdd-guide' }
  )

  await agent(
    `TDD Step 3/3 (full verification) for #${item.id} "${item.title}"\n\n` +
    `**前置条件**：Step 2 已完成。最后兜底：\n` +
    `1. \`DOTNET_CLI_UI_LANGUAGE=en-US dotnet build 10e0.slnx 2>&1 | tail -5\` 必须 0 警告 0 错误\n` +
    `2. \`DOTNET_CLI_UI_LANGUAGE=en-US dotnet test 10e0.slnx --nologo 2>&1 | tail -10\` 必须全绿\n` +
    `3. \`DOTNET_CLI_UI_LANGUAGE=en-US dotnet test 10e0.slnx --collect:"XPlat Code Coverage" 2>&1 | tail -5\` 报告覆盖率数字\n` +
    `4. \`DOTNET_CLI_UI_LANGUAGE=en-US dotnet format 10e0.slnx --verify-no-changes --severity warn 2>&1 | tail -5\` 必须 exit 0（PR #27 WHITESPACE 教训：commit 前必须 format 干净，否则 CI 卡门禁）\n` +
    `**只跑命令 + 报告数字**，不写新代码、不修任何东西。如果不绿就 throw 让上层 fail。\n` +
    `回报：{ buildOk, testsOk, coverage, formatOk } 结构化数据。

**所有 dotnet 命令必须加 \`DOTNET_CLI_UI_LANGUAGE=en-US\` 前缀**，否则在 zh_CN locale 下 CLI 输出中文"已通过!"，"Passed!" 匹配不到，下游 regex 误判失败。`,
    { phase: 'TDD-Verify', agentType: 'general-purpose' }
  )

  // 4. tests (红就 fail) — schema 验证，process-item 直接看 failed 字段，不依赖正则
  const TESTS_SCHEMA = {
    type: 'object',
    required: ['buildOk', 'testsOk', 'failed', 'passed', 'skipped', 'formatOk'],
    properties: {
      buildOk: { type: 'boolean' },
      testsOk: { type: 'boolean' },
      failed: { type: 'number' },
      passed: { type: 'number' },
      skipped: { type: 'number' },
      formatOk: { type: 'boolean' },
      raw: { type: 'string' },
    },
  }
  const testResult = await agent(
    `执行以下 dotnet 命令并回报**结构化结果**（不要只说"全部通过"或"全绿"）：\n\n` +
    `1. \`DOTNET_CLI_UI_LANGUAGE=en-US dotnet build 10e0.slnx 2>&1 | tail -20\`\n` +
    `   → buildOk = exit code 0 AND 输出无 'Error(' 子串\n` +
    `2. \`DOTNET_CLI_UI_LANGUAGE=en-US dotnet test 10e0.slnx --nologo 2>&1 | tail -30\`\n` +
    `   → testsOk = exit code 0 AND 'Failed: 0' 子串存在\n` +
    `   → 从输出 grep 数字: Passed: <n>, Failed: <n>, Skipped: <n>\n` +
    `3. \`DOTNET_CLI_UI_LANGUAGE=en-US dotnet format 10e0.slnx --verify-no-changes --severity warn 2>&1 | tail -5\`\n` +
    `   → formatOk = exit code 0\n\n` +
    `**DOTNET_CLI_UI_LANGUAGE=en-US 必须带**，否则 zh_CN locale 下 CLI 输出"已通过!"，` +
    `grep 不到 'Passed:' 数字。\n\n` +
    `**严格按 schema 返回**（字段名拼写、大小写必须一致，failed/passed/skipped 是 number 不是 string）：\n` +
    `{\n` +
    `  "buildOk": true,\n` +
    `  "testsOk": true,\n` +
    `  "failed": 0,\n` +
    `  "passed": 629,\n` +
    `  "skipped": 1,\n` +
    `  "formatOk": true,\n` +
    `  "raw": "<build+test+format 原始 tail 输出的拼接，用于调试>"\n` +
    `}`,
    { phase: 'Tests', agentType: 'general-purpose', schema: TESTS_SCHEMA }
  )
  // 字段判断（替代旧的正则）：3 个 boolean 都 true 且 failed === 0
  if (!testResult?.buildOk || !testResult?.testsOk || !testResult?.formatOk || (testResult?.failed ?? 0) > 0) {
    throw new Error(
      `tests failed: buildOk=${testResult?.buildOk} testsOk=${testResult?.testsOk} ` +
      `formatOk=${testResult?.formatOk} failed=${testResult?.failed ?? '?'} passed=${testResult?.passed ?? '?'} ` +
      `| ${(testResult?.raw || '').slice(0, 500)}`
    )
  }

  // 5. local review
  await agent(
    `对当前 working tree 的所有未提交改动做本地 code review。` +
    `处理所有 CRITICAL 与 HIGH 级问题；MEDIUM 尽量修；LOW 可记录到 issue。` +
    `完成后总结：处理了哪些问题、跳过了哪些、为什么。`,
    { phase: 'Local Review', agentType: 'code-reviewer' }
  )

  // 6. open PR（**绝对禁止**直接 commit/push 到 main 或 dev）
  const prInfo = await agent(
    `为 #${item.id} 开 PR（当前应已在 feature 分支 \`${workBranch}\`）：\n\n` +
    `**dev/main 策略（违反任意一条就 throw）**：\n` +
    `1. base **必须**是 dev，**绝不能**是 main\n` +
    `2. head **必须**是当前 feature 分支 \`${workBranch}\`，**绝不能**是 main 或 dev\n` +
    `3. 如果当前分支是 main 或 dev，立刻 throw "agentic 禁止从 main/dev 开 PR！"\n` +
    `4. 推送用 \`git push -u origin ${workBranch}\`（**绝不能** \`git push origin main/dev\`）\n\n` +
    `PR 内容：\n` +
    `- 标题: <type>: ${item.title}（格式参考 git-workflow.md）\n` +
    `- 正文: 关联 #${item.id}（Closes #N 或 Refs #N）、测试计划、checklist\n` +
    `- 用 mcp__github__create_pull_request 创建\n\n` +
    `回报字段：{ prNumber: number, prUrl: string, headBranch: string, baseBranch: string }`,
    { phase: 'Open PR', agentType: 'general-purpose', schema: {
      type: 'object',
      required: ['prNumber', 'prUrl', 'headBranch', 'baseBranch'],
      properties: {
        prNumber: { type: 'number' },
        prUrl: { type: 'string' },
        headBranch: { type: 'string' },
        baseBranch: { type: 'string' },
      },
    } }
  )

  const prNumber = prInfo.prNumber
  const prUrl = prInfo.prUrl
  if (!prNumber) throw new Error(`PR 创建失败: ${JSON.stringify(prInfo)}`)

  // 7. 盯自动 review（inline agent 轮询 claude-review.yml；harness 限制 child workflow 不能再嵌套调 workflow）
  const reviewTimeoutMs = args.reviewTimeoutMs ?? 900000  // 默认 15 分钟
  const REVIEW_SCHEMA = {
    type: 'object',
    required: ['items'],
    properties: {
      items: {
        type: 'array',
        items: {
          type: 'object',
          required: ['id', 'user', 'body', 'state'],
          properties: {
            id: { type: 'number' },
            user: { type: 'string' },
            body: { type: 'string' },
            path: { type: 'string' },
            line: { type: 'number' },
            state: { type: 'string', enum: ['COMMENTED', 'APPROVED', 'CHANGES_REQUESTED', 'PENDING', 'DISMISSED'] },
          },
        },
      },
    },
  }
  // 轮询次数由 shell 的 seq 控制（确定性），不让 LLM 自己心算 sleep 次数——
  // 否则 MiniMax-M3 会在 CI 早已 settled 后仍无限空转（实测卡 >40 分钟）。每次间隔 20s。
  const pollCount = Math.max(3, Math.floor(reviewTimeoutMs / 20000))
  const reviewResult = await agent(
    `盯 PR #${prNumber} (${prUrl}) 的 CI + claude-review.yml 完成，然后拉所有 review 评论。\n\n` +
    `**第一步：原样执行下面这段 shell 轮询（循环次数由 \`seq\` 控制，绝对不要自己心算 sleep 次数、不要在循环外再反复查询）：**\n` +
    '```bash\n' +
    `PR=${prNumber}\n` +
    `for i in $(seq 1 ${pollCount}); do\n` +
    `  pending=$(gh pr checks $PR --json bucket -q '[.[] | select(.bucket=="pending")] | length' 2>/dev/null || echo err)\n` +
    `  echo "poll $i/${pollCount}: pending=$pending"\n` +
    `  [ "$pending" = "0" ] && { echo "checks settled"; break; }\n` +
    `  sleep 20\n` +
    `done\n` +
    `gh pr checks $PR --json name,state,bucket\n` +
    '```\n\n' +
    `**第二步：checks settled（或循环用尽）后，拉 review 数据：**\n` +
    '```bash\n' +
    `gh pr view ${prNumber} --json reviews\n` +
    `gh api repos/{owner}/{repo}/pulls/${prNumber}/comments\n` +
    '```\n\n' +
    `**第三步**：把 reviews（每条取 author.login→user, body, state）和行内 comments（user.login→user, body, path, line，state 填 "COMMENTED"）合并成数组，按 schema 返回。\n` +
    `返回 schema: { items: [{ id, user, body, path?, line?, state }] }（数组放在 items 字段下，不是顶层数组）。\n` +
    `**shell 循环一结束就立刻进第二步**；没有任何 review/comment 就返回 { items: [] }。`,
    { phase: 'Watch Review', schema: REVIEW_SCHEMA, agentType: 'general-purpose' }
  )
  const reviews = reviewResult?.items ?? []
  log(`PR #${prNumber} 收到 ${reviews.length} 条 review`)

  // 8. 分类处理 review
  const handleResult = await agent(
    `处理 PR #${prNumber} 的 ${reviews.length} 条 review 评论：\n\n` +
    `分类原则：\n` +
    `- 拼写/typo/命名、局部重构、注释补充、测试覆盖 → 直接修，commit push 到同 PR（${workBranch}）\n` +
    `- 架构/大重构、新功能建议、跨 PR 范围、设计争论 → 开新 issue，标题 "Followup from #${prNumber}: <摘要>"，` +
    `  标签 followup-from:#${prNumber} + 对应 enhancement/refactor 标签\n` +
    `  正文包含：来源 PR、review 链接、为什么不在本 PR、建议处理\n\n` +
    `Reviews：\n${JSON.stringify(reviews, null, 2)}\n\n` +
    `回报字段：followupCount (int, 开的新 issue 数)、fixedCount (int, 本 PR 修的评论数)、summary (string, 摘要)。`,
    { phase: 'Handle Review', agentType: 'general-purpose' }
  )

  // 9. 合并 PR 到 dev + 同步本地 dev（用户选择全自动）
  //    门禁：必须 CI(pr-build.yml) 绿 + mergeable，否则不合并（返回 reason，不强合）。
  //    branch protection 若要求人工 review approval，merge API 会 422 → 标记需人工 approve，不崩溃。
  const MERGE_SCHEMA = {
    type: 'object',
    required: ['merged'],
    properties: {
      merged: { type: 'boolean' },
      mergeSha: { type: 'string' },
      devSynced: { type: 'boolean' },
      reason: { type: 'string' },
    },
  }
  const mergeResult = await agent(
    `合并 PR #${prNumber} 到 dev 并同步本地 dev 分支。\n\n` +
    `**Watch Review 阶段已等过 CI，这里只查一次、不再轮询：**\n` +
    `1. \`gh pr checks ${prNumber} --json name,state,bucket\` —— 是否所有 bucket 都是 pass/skipping（**无 pending/fail**）\n` +
    `   \`gh pr view ${prNumber} --json mergeable,mergeStateStatus\` —— mergeable 是否 MERGEABLE\n` +
    `2. 若有 check fail/pending 或不可合并 → 返回 { merged:false, reason:"<具体状态>" }，**不要强合**\n` +
    `3. 若全 pass 且 MERGEABLE → \`gh pr merge ${prNumber} --squash\`\n` +
    `   （本仓库 dev 启用 linear history，**必须 --squash**，不能 merge commit）\n` +
    `   - 若报错含 "not mergeable" / "required" / "review" / "protected" / "422" →\n` +
    `     返回 { merged:false, reason:"branch protection 需人工 approve 后才能合并" }，**不要崩溃**\n` +
    `4. merge 成功后同步本地：\`git checkout dev\` → \`git pull --ff-only origin dev\`（devSynced=true）\n` +
    `   再删分支：\`git branch -D ${workBranch}\` + \`git push origin --delete ${workBranch}\`（删除失败可忽略）\n\n` +
    `回报 schema：{ merged: boolean, mergeSha?: string, devSynced?: boolean, reason?: string }`,
    { phase: 'Merge & Sync', agentType: 'general-purpose', schema: MERGE_SCHEMA }
  )
  log(`PR #${prNumber} 合并: merged=${mergeResult.merged} devSynced=${mergeResult.devSynced ?? false} ${mergeResult.reason ?? ''}`)

  // 显式使用全部字段（linter 看不到顶层 return 是 consumer）
  log(`#${item.id} 处理完成: pr=#${prNumber} merged=${mergeResult.merged} ` +
    `followup=${handleResult.followupCount ?? 0} fixed=${handleResult.fixedCount ?? 0} | ${handleResult.summary ?? ''}`)

  return {
    ok: true,
    prNumber,
    prUrl,
    merged: Boolean(mergeResult.merged),
    mergeReason: mergeResult.reason ?? '',
    followupCount: handleResult.followupCount ?? 0,
    fixedCount: handleResult.fixedCount ?? 0,
    summary: handleResult.summary ?? '',
  }
} catch (e) {
  // 单 item 失败：不崩溃整个 triage-loop，返回 ok:false 让上层跳过继续。
  // 工作区残留无需在此清理——下一个 item 的 BranchCheck 会强制 `git checkout -f dev`。
  const msg = e?.message ?? String(e)
  log(`#${item.id} 处理失败（已捕获，跳过该项）: ${msg}`)
  return { ok: false, prNumber: null, merged: false, followupCount: 0, error: msg }
}
