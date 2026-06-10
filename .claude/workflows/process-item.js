// 单 issue/PR 完整 7 步工作流
// 1. BDD      bdd-guide  写验收测试 (RED)
// 2. TDD      tdd-guide   实现让测试 GREEN
// 3. tests    general-purpose 跑 dotnet build/test
// 4. review   code-reviewer / simplify  本地扫一遍
// 5. open PR  general-purpose  开 PR 到 dev
// 6. watch    wait-for-pr-review 子工作流
// 7. handle   general-purpose 分类处理 review
//
// 设计：worktree 由 process-item 顶层创建（isolation: 'worktree'），
//       内部 7 步全部接力同一 worktree（不带 isolation）。
//       第 5 步开 PR 时 push 到 feature 分支并合入远端。
//
// 派单策略（dispatchKind）已内联到本文件底部（harness 不支持 relative import）
//
// 输入：args.item = { id, type, url, title, body, labels, priority }
// 输出：{ ok, prNumber, prUrl, followupCount, error? }

export const meta = {
  name: 'process-item',
  description: '单 issue/PR 走完整 7 步：BDD→TDD→tests→review→PR→盯 review→分类',
  phases: [
    { title: 'BranchCheck' },
    { title: 'Dispatch' },
    { title: 'Planner' },
    { title: 'BDD' },
    { title: 'TDD-Schema' },
    { title: 'TDD-Impl' },
    { title: 'TDD-Verify' },
    { title: 'Local Review' },
    { title: 'Open PR' },
    { title: 'Watch Review' },
    { title: 'Handle Review' },
  ],
}

// —— 内联自 lib/dispatch.js（harness 工作流发现机制可能不支持 relative import，
//    所有工作流文件必须自包含，参考 triage-loop.js / wait-for-pr-review.js）
// 注意：BUG_LABELS 故意不含 p0/p1——优先级标签不代表 bug 类型
// issue #7 案例：label = [enhancement, p1, security] 命中 'feature' 走完整 7 步 + planner
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

// —— preflight：分支策略校验（防止 agent 在 main/dev 上直接开发）
// 规则：本仓库 main 仅用于发版，dev 是集成分支，所有 feature 必须在 feature/* 分支上开发并开 PR 到 dev
const branchCheck = await agent(
  `执行 \`git rev-parse --abbrev-ref HEAD\` 和 \`git status --porcelain\`。\n\n` +
  `**硬性规则**：\n` +
  `1. 当前分支**禁止**是 main 或 dev。如果在 main/dev，立即 throw，错误信息："agentic 禁止在 main/dev 直接开发！请创建 feature/* 分支"\n` +
  `2. 当前分支应是 feature/issue-N-slug 格式（slug 是 issue 标题的 kebab-case）。如果不是，**自动创建**：\n` +
  `   \`git checkout -b feature/${item.id}-${(item.title || 'fix').toLowerCase().replace(/[^a-z0-9]+/g,'-').replace(/^-+|-+$/g,'').slice(0,40)}\`\n` +
  `3. 工作区必须干净（无未提交改动）。如果有未提交改动，throw 让上层处理。\n\n` +
  `回报：{ currentBranch, isFeature, worktreeClean, createdBranch? }`,
  { phase: 'BranchCheck', agentType: 'general-purpose' }
)
log(`分支 preflight: ${JSON.stringify(branchCheck)}`)

const kind = dispatchKind(item)
log(`#${item.id} 派单: ${kind} (${item.title})`)

// stale / 简单 issue 不走完整 7 步
if (kind === 'stale') {
  await agent(
    `判断 issue #${item.id} (${item.title}) 是否需要关闭或 ping 作者。` +
    `如果超过 30 天无活动且无进展评论，添加 stale label 并评论"如无更新将在 7 天后关闭"。`,
    { phase: 'Dispatch', agentType: 'issue-prioritizer' }
  )
  return { ok: true, prNumber: null, followupCount: 0 }
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

// 2. TDD
if (NEEDS_PLANNER_KINDS.has(kind)) {
  // feature 大改，先规划
  await agent(
    `为 #${item.id} "${item.title}" 写实现 plan：涉及哪些模块、新增哪些类型、` +
    `对现有 API 的影响、是否需要数据库迁移。产出 plan markdown 不超过 200 行。`,
    { phase: 'Dispatch', agentType: 'planner' }
  )
}

// 3 步 TDD（避免单 agent 撑爆 context）：
//   1. schema/接口（DB 模型、新接口签名、DI 注册）—— 改动 < 5 文件
//   2. 实现（handler/service/evaluator 改写）—— 改动 < 5 文件
//   3. 单元测试补全（覆盖边界条件）+ 跑全 suite
// 每步独立 agent，靠文件系统接力；如果上一步没产出，本步就少干点
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

// 3. tests (红就回 TDD)
const testResult = await agent(
  `执行 \`DOTNET_CLI_UI_LANGUAGE=en-US dotnet build 10e0.slnx 2>&1 | tail -20\`，然后 \`DOTNET_CLI_UI_LANGUAGE=en-US dotnet test 10e0.slnx --nologo 2>&1 | tail -30\`，再 \`DOTNET_CLI_UI_LANGUAGE=en-US dotnet format 10e0.slnx --verify-no-changes --severity warn 2>&1 | tail -5\`。` +
  `回报：是否 build 成功、所有测试是否通过、format 校验是否 exit 0、覆盖率数字。

**DOTNET_CLI_UI_LANGUAGE=en-US 必须带**，否则在 zh_CN locale 下 CLI 输出中文"已通过!"，"Passed!" 匹配不到，下游 regex 误判失败。`,
  { phase: 'Tests', agentType: 'general-purpose' }
)
// 兜底正则：中英文都匹配（即使 agent 漏加 env var）
if (!/Passed!|Test execution time|tests? passed|已通过/i.test(testResult) || /Failed!|失败:\s*[1-9]/i.test(testResult)) {
  throw new Error(`#${item.id} tests failed: ${testResult.slice(0, 500)}`)
}

// 4. local review
await agent(
  `对当前 working tree 的所有未提交改动做本地 code review。` +
  `处理所有 CRITICAL 与 HIGH 级问题；MEDIUM 尽量修；LOW 可记录到 issue。` +
  `完成后总结：处理了哪些问题、跳过了哪些、为什么。`,
  { phase: 'Local Review', agentType: 'code-reviewer' }
)

// 5. open PR（**绝对禁止**直接 commit/push 到 main 或 dev）
const prInfo = await agent(
  `为 #${item.id} 开 PR：\n\n` +
  `**dev/main 策略（违反任意一条就 throw）**：\n` +
  `1. base **必须**是 dev，**绝不能**是 main\n` +
  `2. head **必须**是当前 feature/issue-N-* 分支，**绝不能**是 main 或 dev\n` +
  `3. 如果当前分支是 main 或 dev，立刻 throw "agentic 禁止从 main/dev 开 PR！"\n` +
  `4. 推送用 \`git push -u origin <feature-branch>\`（**绝不能** \`git push origin main/dev\`）\n\n` +
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
if (!prNumber) throw new Error(`#${item.id} PR 创建失败: ${JSON.stringify(prInfo)}`)

// 6. 盯自动 review（子工作流轮询 claude-review.yml）
const reviews = await workflow('wait-for-pr-review', {
  prNumber,
  prUrl,
  timeoutMs: args.reviewTimeoutMs ?? 900000,  // 默认 15 分钟
})

log(`PR #${prNumber} 收到 ${reviews.length} 条 review`)

// 7. 分类处理 review
const handleResult = await agent(
  `处理 PR #${prNumber} 的 ${reviews.length} 条 review 评论：\n\n` +
  `分类原则：\n` +
  `- 拼写/typo/命名、局部重构、注释补充、测试覆盖 → 直接修，commit push 到同 PR\n` +
  `- 架构/大重构、新功能建议、跨 PR 范围、设计争论 → 开新 issue，标题 "Followup from #${prNumber}: <摘要>"，` +
  `  标签 followup-from:#${prNumber} + 对应 enhancement/refactor 标签\n` +
  `  正文包含：来源 PR、review 链接、为什么不在本 PR、建议处理\n\n` +
  `Reviews：\n${JSON.stringify(reviews, null, 2)}\n\n` +
  `回报字段：followupCount (int, 开的新 issue 数)、fixedCount (int, 本 PR 修的评论数)、summary (string, 摘要)。`,
  { phase: 'Handle Review', agentType: 'general-purpose' }
)

// 显式使用全部字段（linter 看不到顶层 return 是 consumer）
log(`PR #${prNumber} 处理完成: followup=${handleResult.followupCount ?? 0} fixed=${handleResult.fixedCount ?? 0} | ${handleResult.summary ?? ''}`)

return {
  ok: true,
  prNumber,
  prUrl,
  followupCount: handleResult.followupCount ?? 0,
  fixedCount: handleResult.fixedCount ?? 0,
  summary: handleResult.summary ?? '',
}
