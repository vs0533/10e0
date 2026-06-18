// 单 issue/PR 完整工作流（meta.phases 列表是事实源）
//   BranchCheck → Dispatch → [Planner] → BDD → TDD-Schema → TDD-Impl → TDD-Verify
//   → Tests → Local Review → Open PR → [review-fix 循环: Watch Review → (REQUEST_CHANGES 时) Handle Review]
// 末段 Watch/Handle/Merge 是一个**循环**（最多 maxReviewRounds 轮）：
//   等 CI + 解析 bot VERDICT → APPROVE 才合并；REQUEST_CHANGES 则修能修的+push 重等、不能修的开 followup issue；
//   NONE 不合并留人工。**收紧门禁：只有 bot 明确 APPROVE 才自动合并。**
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
  // 加固：防 agent 没真跑 dotnet test 就空报 testsOk:true / failed:0。
  // raw 必须含真实 dotnet test 输出特征（Passed!/Passed: <n>/Failed: <n>），否则字段不可信。
  const testsRaw = String(testResult?.raw || '')
  if (!/Passed!|Passed:\s*\d+|Failed:\s*\d+/.test(testsRaw)) {
    throw new Error(
      `#${item.id} tests 可疑：testsOk=${testResult?.testsOk} 但 raw 无真实 dotnet test 输出特征` +
      `（疑似未真跑测试，schema 字段不可信）| raw=${testsRaw.slice(0, 500)}`
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
    `- 正文（防重复处理）：${item.type === 'issue' ? '**必须**包含 "Closes #' + item.id + '"——合并后自动关闭 issue #' + item.id + '，否则它遗留 open 会被下轮 triage 重复处理、与已合并改动冲突（#51/#55/#58 教训）' : '用 "Refs #' + item.id + '"（PR 类型不要 Closes 自己）'}；附测试计划、checklist\n` +
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

  // 7-9. review-fix 循环（收紧门禁 + 自动修复重试）：
  //   每轮：等 CI settled → 拉三类评论 + 解析 bot VERDICT →
  //     - APPROVE：CI 绿 + mergeable 时 squash 合并到 dev + 同步本地，结束
  //     - REQUEST_CHANGES：逐条评估——能在本 PR 修的就修 + git push（触发新 CI + 新 review）→ 下一轮重等；
  //                        不能修的开可追溯 followup issue。本轮没 push 任何修复 → 停（留人工，issue 已追溯）
  //     - NONE（bot 没产出 VERDICT）：收紧模式下不合并，留人工
  //   最多 maxReviewRounds 轮，防止无限循环（每轮含等 CI + bot review + 修复，注意 budget）。
  //
  // **收紧门禁（用户要求）**：只有 bot 明确 APPROVE 才自动合并；REQUEST_CHANGES / NONE 一律不自动合。
  const reviewTimeoutMs = args.reviewTimeoutMs ?? 900000  // 默认 15 分钟
  const maxReviewRounds = args.maxReviewRounds ?? 3
  const pollCount = Math.max(3, Math.floor(reviewTimeoutMs / 20000))  // shell seq 控制轮询次数，间隔 20s
  const REVIEW_SCHEMA = {
    type: 'object',
    required: ['items', 'botVerdict'],
    properties: {
      // claude-review.yml bot 的结论：APPROVE / REQUEST_CHANGES（有 🔴 Critical）/ NONE（没找到 bot 评论）
      botVerdict: { type: 'string', enum: ['APPROVE', 'REQUEST_CHANGES', 'NONE'] },
      // PR 与 dev 的可合并状态（用于冲突门禁，防自愈循环对着 merge 冲突空转）
      mergeable: { type: 'string' },         // MERGEABLE / CONFLICTING / UNKNOWN
      mergeStateStatus: { type: 'string' },  // CLEAN / DIRTY / BLOCKED / BEHIND / UNSTABLE / UNKNOWN
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
  // Handle 结构化回报：pushed/fixedCount 决定要不要重等 CI（代码没变就别空等）
  const HANDLE_SCHEMA = {
    type: 'object',
    required: ['fixedCount', 'followupCount', 'pushed'],
    properties: {
      fixedCount: { type: 'number' },      // 本轮在当前 PR 改代码并已 git push 的评论数
      followupCount: { type: 'number' },   // 本轮开的可追溯 followup issue 数
      pushed: { type: 'boolean' },         // 本轮是否真的 git push 了修复
      summary: { type: 'string' },
    },
  }
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

  let merged = false
  let mergeReason = ''
  let finalVerdict = 'NONE'
  let totalFixed = 0
  let totalFollowup = 0
  let lastSummary = ''

  for (let round = 1; round <= maxReviewRounds; round++) {
    log(`PR #${prNumber} review 轮 ${round}/${maxReviewRounds}`)

    // (1) 等 CI settled + 拉三类评论 + 解析 bot VERDICT
    const reviewResult = await agent(
      `盯 PR #${prNumber} (${prUrl}) 的 CI + claude-review.yml 完成（第 ${round} 轮），拉所有评论 + 解析 bot 结论。\n\n` +
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
      `**第二步：拉三类评论（缺一不可——bot 可能发正式 review，也可能降级成 issue comment）+ PR 可合并状态：**\n` +
      '```bash\n' +
      `gh pr view ${prNumber} --json reviews\n` +
      `gh api repos/{owner}/{repo}/pulls/${prNumber}/comments\n` +
      `gh api repos/{owner}/{repo}/issues/${prNumber}/comments\n` +
      `gh pr view ${prNumber} --json mergeable,mergeStateStatus    # 冲突门禁用\n` +
      '```\n\n' +
      `**第三步**：合并评论成 items（reviews→author.login/body/state；行内 comment→user.login/body/path/line,state="COMMENTED"；issue comment→user.login/body,state="COMMENTED"）。\n` +
      `把 \`gh pr view --json mergeable,mergeStateStatus\` 的两个值**原样**填进返回的 mergeable / mergeStateStatus 字段。\n` +
      `**只按时间最新的那条 bot 评论判 verdict**（本 PR 可能已 push 过多轮，别被旧评论干扰）。\n\n` +
      `**第四步：解析最新 bot VERDICT**（bot 评论特征："🤖 MiniMax Code Review" / "VERDICT:" / "Verdict:"）：\n` +
      `- 含 "VERDICT: REQUEST_CHANGES" / "Verdict: **REQUEST_CHANGES**" → "REQUEST_CHANGES"\n` +
      `- 含 "VERDICT: APPROVE" / "Verdict: **APPROVE**" → "APPROVE"\n` +
      `- 找不到 bot 评论 → "NONE"\n\n` +
      `返回 schema: { botVerdict, mergeable, mergeStateStatus, items: [{ id, user, body, path?, line?, state }] }（数组放 items 字段下）。无评论返回 { botVerdict:"NONE", items: [], mergeable, mergeStateStatus }。`,
      { phase: 'Watch Review', schema: REVIEW_SCHEMA, agentType: 'general-purpose' }
    )
    const reviews = reviewResult?.items ?? []
    const botVerdict = reviewResult?.botVerdict ?? 'NONE'
    finalVerdict = botVerdict
    log(`PR #${prNumber} 轮 ${round}: ${reviews.length} 条评论, verdict=${botVerdict}, mergeable=${reviewResult?.mergeable ?? '?'}/${reviewResult?.mergeStateStatus ?? '?'}`)

    // (2a) 冲突门禁：PR 与 dev 冲突（CONFLICTING/DIRTY）→ 自愈循环修不了 merge 冲突，立即停留人工。
    //      常见根因：重复处理了已被合并 PR 解决的 issue（#51/#55/#58 案例），或 base 落后需 rebase。
    if (reviewResult?.mergeable === 'CONFLICTING' || reviewResult?.mergeStateStatus === 'DIRTY') {
      mergeReason = `PR #${prNumber} 与 dev 冲突（mergeable=${reviewResult?.mergeable}, state=${reviewResult?.mergeStateStatus}）——自愈循环修不了 merge 冲突，停止重试留人工（疑似重复处理已合并 issue，或需 rebase）`
      log(`PR #${prNumber} 检测到冲突，停止自愈循环：${mergeReason}`)
      break
    }

    // (2) 收紧门禁：只有 APPROVE 才尝试合并
    if (botVerdict === 'APPROVE') {
      const mergeResult = await agent(
        `合并 PR #${prNumber} 到 dev 并同步本地 dev 分支。\n\n` +
        `**已等过 CI，这里只查一次、不再轮询：**\n` +
        `1. \`gh pr checks ${prNumber} --json name,state,bucket\` —— 是否所有 bucket 都是 pass/skipping（**无 pending/fail**）\n` +
        `   \`gh pr view ${prNumber} --json mergeable,mergeStateStatus\` —— mergeable 是否 MERGEABLE\n` +
        `2. 若有 check fail/pending 或不可合并 → 返回 { merged:false, reason:"<具体状态>" }，**不要强合**\n` +
        `3. 若全 pass 且 MERGEABLE → \`gh pr merge ${prNumber} --squash\`\n` +
        `   （本仓库 dev 启用 linear history，**必须 --squash**，不能 merge commit）\n` +
        `   - 报错含 "not mergeable"/"required"/"review"/"protected"/"422" → 返回 { merged:false, reason:"branch protection 需人工 approve" }，**不崩溃**\n` +
        `4. merge 成功后：\`git checkout dev\` → \`git pull --ff-only origin dev\`（devSynced=true）\n` +
        `   再删分支：\`git branch -D ${workBranch}\` + \`git push origin --delete ${workBranch}\`（失败忽略）\n\n` +
        `回报 schema：{ merged: boolean, mergeSha?: string, devSynced?: boolean, reason?: string }`,
        { phase: 'Merge & Sync', agentType: 'general-purpose', schema: MERGE_SCHEMA }
      )
      merged = Boolean(mergeResult.merged)
      mergeReason = mergeResult.reason ?? ''
      log(`PR #${prNumber} APPROVE → 合并: merged=${merged} ${mergeReason}`)
      break  // APPROVE 后结束循环（合并失败是 CI/protection 问题，reason 已记，留人工）
    }

    // (3) NONE：bot 没产出 VERDICT，收紧模式不合并；重试结果也是 NONE（代码没变），直接停留人工
    if (botVerdict === 'NONE') {
      mergeReason = `bot 未产出 VERDICT（NONE）——收紧模式只认 APPROVE，留人工检查 claude-review 是否异常`
      log(`PR #${prNumber} verdict=NONE，${mergeReason}`)
      break
    }

    // (4) REQUEST_CHANGES：逐条评估，能在本 PR 修的就修 + push，不能修的开可追溯 followup issue
    const handleResult = await agent(
      `PR #${prNumber} 第 ${round} 轮收到 bot REQUEST_CHANGES（存在 🔴 Critical）。逐条评估并处理：\n\n` +
      `**判定「能否在当前 PR (${workBranch}) 解决」：**\n` +
      `- ✅ 能解决（bug、拼写/命名、局部重构、补测试、缺 XML doc 等）→ **在当前分支改代码，commit 并 \`git push\`**（push 触发新 CI + 新 claude-review）\n` +
      `- ❌ 不能在本 PR 解决（架构大改、新功能、跨 PR 范围、设计争论）→ **开 followup issue 保证可追溯**：\n` +
      `    - 标题 "Followup from #${prNumber}: <摘要>"\n` +
      `    - 标签 \`followup-from:#${prNumber}\` + enhancement/refactor\n` +
      `    - 正文：来源 PR #${prNumber} 链接、对应 review 评论摘要/链接、为什么不能在本 PR 解决、建议处理方式\n\n` +
      `bot 评论 + 所有 review：\n${JSON.stringify(reviews, null, 2)}\n\n` +
      `**严格统计**：fixedCount 只数「真改了代码且 git push 成功」的；pushed=本轮是否执行过 git push（决定要不要重等 CI）。\n` +
      `回报 schema：{ fixedCount, followupCount, pushed, summary }。`,
      { phase: 'Handle Review', schema: HANDLE_SCHEMA, agentType: 'general-purpose' }
    )
    const fixed = handleResult.fixedCount ?? 0
    const followup = handleResult.followupCount ?? 0
    totalFixed += fixed
    totalFollowup += followup
    lastSummary = handleResult.summary ?? ''
    log(`PR #${prNumber} 轮 ${round} Handle: fixed=${fixed} followup=${followup} pushed=${handleResult.pushed}`)

    // (5) 本轮没 push 任何修复 → 重等 CI/verdict 结果不会变（代码没动）→ 停，留人工（followup issue 已开可追溯）
    if (!handleResult.pushed || fixed === 0) {
      mergeReason = `REQUEST_CHANGES 的问题无法在本 PR 自动解决（已开 ${totalFollowup} 个 followup issue 追溯），留人工`
      log(`PR #${prNumber} 本轮无 push 修复，停止重试：${mergeReason}`)
      break
    }
    // fixed>0 且 pushed：回循环顶，重新等新 CI + 新 bot verdict，直到 APPROVE 或耗尽轮数
    log(`PR #${prNumber} 本轮 push 了 ${fixed} 处修复，进入下一轮重等 CI + verdict`)
  }

  // 循环耗尽仍未拿到 APPROVE
  if (!merged && !mergeReason) {
    mergeReason = `review 重试 ${maxReviewRounds} 轮仍未拿到 APPROVE（最后 verdict=${finalVerdict}），留人工`
  }

  // 显式使用全部字段（linter 看不到顶层 return 是 consumer）
  log(`#${item.id} 处理完成: pr=#${prNumber} merged=${merged} verdict=${finalVerdict} ` +
    `fixed=${totalFixed} followup=${totalFollowup} | ${lastSummary} | ${mergeReason}`)

  return {
    ok: true,
    prNumber,
    prUrl,
    merged,
    mergeReason,
    botVerdict: finalVerdict,
    followupCount: totalFollowup,
    fixedCount: totalFixed,
    summary: lastSummary,
  }
} catch (e) {
  // 单 item 失败：不崩溃整个 triage-loop，返回 ok:false 让上层跳过继续。
  // 工作区残留无需在此清理——下一个 item 的 BranchCheck 会强制 `git checkout -f dev`。
  const msg = e?.message ?? String(e)
  log(`#${item.id} 处理失败（已捕获，跳过该项）: ${msg}`)
  return { ok: false, prNumber: null, merged: false, followupCount: 0, error: msg }
}
