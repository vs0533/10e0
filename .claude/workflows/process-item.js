// 单 issue/PR 完整工作流（meta.phases 列表是事实源）
//   BranchCheck → Dispatch → [feature: Planner + 动态分支]
//   → BDD → TDD → Tests → Local Review → Open PR
//   → [review-fix 循环: Watch Review → (REQUEST_CHANGES 时) Handle Review]
//   → Merge & Sync
// 末段 Watch/Handle/Merge 是一个**循环**（最多 maxReviewRounds 轮）：
//   等 CI + 解析 bot VERDICT → APPROVE 才合并；REQUEST_CHANGES 则修能修的+push 重等、不能修的开 followup issue；
//   NONE 不合并留人工。**收紧门禁：只有 bot 明确 APPROVE 才自动合并。**
// 派单策略（dispatchKind）已内联到本文件底部（harness 不支持 relative import）
//
// **feature L2/L3 动态分支**（仅 feature 类型，其它 kind 不走 Planner，直接默认固定 2 步 TDD）：
//   Planner 用 PLAN_SCHEMA 返回结构化 {decomposable, reason, steps?, subIssues?}。
//   - L2：decomposable=true + steps[] → plan-driven 多步 TDD（每步 <5 文件，总量不限）；
//          步数由 maxSteps（默认 6）限上限，超出 trim。
//   - L3：decomposable=false + subIssues[] → 自动建有序子 issue（followup-from:#<epic> + 依赖标注），
//          原 issue 转 tracking epic（加 `epic` 标签 + checklist body），提前 return { decomposed:true }
//          让 triage-loop 把它当"妥善处理"跳过该 issue。
//
// worktree 策略（与 SKILL.md 一致）：
//   triage-loop.js 调本工作流时**不带** isolation——本工作流顺序执行，所有阶段
//   接力同一 workspace（即用户主仓库工作目录），靠 git 分支做跨 item 隔离。
//   因此每个 item 必须由 BranchCheck 强制回到干净的最新 dev 后再切 feature 分支，
//   否则会发生「第 N 个 item 的分支基于第 N-1 个 item 分支」的基线污染。
//
// 输入：args.item = { id, type, url, title, body, labels, priority }
//       args.maxSteps?: number —— plan-driven TDD 步数上限（仅 feature，默认 6）
// 输出：
//   成功合并：{ ok:true, prNumber, prUrl, merged:true, followupCount, fixedCount, botVerdict }
//   L3 拆分：{ ok:true, decomposed:true, splitInto:[sub_issue_numbers], merged:false }
//   失败：{ ok:false, error, savedBranch? } —— 让 triage-loop 跳过本项继续，而不是崩溃整个循环。

export const meta = {
  name: 'process-item',
  description: '单 issue/PR 走完整流程：BranchCheck→BDD→TDD→tests→review→PR→盯 review→分类→合并到 dev+同步本地（feature 含 L2/L3 动态分支）',
  phases: [
    { title: 'BranchCheck' },
    { title: 'Dispatch' },
    { title: 'Planner' },
    { title: 'Decompose to Epic' },  // L3 拆分时才走（动态 phase，feature decomposable=false）
    { title: 'BDD' },
    { title: 'TDD-Schema' },         // 仅默认 2 步；plan-driven 不走此 phase
    { title: 'TDD-Impl' },           // 默认第 2 步 或 plan-driven 多步共用此名前缀
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

// PLAN_SCHEMA：Planner agent 产出的结构化计划（取代旧的「写 plan markdown」）。
//   decomposable=true  → L2 plan-driven 多步 TDD（steps[]，每步 ≤5 文件）
//   decomposable=false → L3 自动拆有序子 issue（subIssues[]），原 issue 转 tracking epic
// reason 字段记录判定依据（review 时可核对，避免 planner 拍脑袋）
const PLAN_SCHEMA = {
  type: 'object',
  required: ['decomposable', 'reason'],
  properties: {
    decomposable: { type: 'boolean' },
    reason: { type: 'string' },
    steps: {
      type: 'array',
      items: {
        type: 'object',
        required: ['title', 'description'],
        properties: {
          title: { type: 'string', minLength: 1 },
          description: { type: 'string' },
          files: { type: 'array', items: { type: 'string' } },
        },
      },
    },
    subIssues: {
      type: 'array',
      items: {
        type: 'object',
        required: ['title', 'description'],
        properties: {
          title: { type: 'string', minLength: 1 },
          description: { type: 'string' },
          dependsOn: { type: 'array', items: { type: 'number' } },  // 序号 0-indexed 指向同数组内其它 sub-issue
          labels: { type: 'array', items: { type: 'string' } },
        },
      },
    },
  },
}

// L3 split 拆 4 个独立 agent + 各自 schema + 严格成功判据（避免 5 步压一 agent 的高风险）：
//   1. ensureLabels    —— gh label list/create 防护（CRITICAL #1）
//   2. createSubs      —— 建子 issue（CRITICAL #2 part A）
//   3. linkDeps        —— 回填依赖（CRITICAL #2 part B，独立因为序号↔号转换易错）
//   4. makeEpic        —— 原 issue 转 epic（CRITICAL #2 part C，关键不变量）
// 关键不变量「ensureLabels.ok && createSubs.createdCount≥1 && makeEpic.epicLabeled=true」满足才 decomposed:true。
const L3_ENSURE_LABELS_SCHEMA = {
  type: 'object',
  required: ['ok', 'followupFromExists', 'epicExists'],
  properties: {
    ok: { type: 'boolean' },
    followupFromExists: { type: 'boolean' },
    epicExists: { type: 'boolean' },
    errors: { type: 'array', items: { type: 'string' } },
  },
}
const L3_CREATE_SUBS_SCHEMA = {
  type: 'object',
  required: ['createdCount', 'splitInto'],
  properties: {
    createdCount: { type: 'number' },
    // items 允许 null 占位（createSubs prompt 要求失败的 sub-issue 位置填 null，供 linkDeps 步骤对齐序号）
    splitInto: { type: 'array', items: { type: ['number', 'null'] } },
    errors: { type: 'array', items: { type: 'string' } },
  },
}
const L3_LINK_DEPS_SCHEMA = {
  type: 'object',
  required: ['linked'],
  properties: {
    linked: { type: 'number' },
    errors: { type: 'array', items: { type: 'string' } },
  },
}
const L3_MAKE_EPIC_SCHEMA = {
  type: 'object',
  required: ['epicLabeled'],
  properties: {
    epicLabeled: { type: 'boolean' },
    bodyRewritten: { type: 'boolean' },
    errors: { type: 'array', items: { type: 'string' } },
  },
}
// L3 失败补救（H1）：catch 块调补救 agent 给原 issue + 已建 sub-issue 加 epic 标签防重复派单。
// 提到顶部常量与 L3_*_SCHEMA 系列并列，保持命名/结构一致。
const L3_REMEDIATE_SCHEMA = {
  type: 'object',
  required: ['ok', 'subIssueLabeledCount'],
  properties: {
    ok: { type: 'boolean' },
    subIssueLabeledCount: { type: 'number' },
    errors: { type: 'array', items: { type: 'string' } },
  },
}

// args 容错：harness 有时把 args 序列化成 JSON 字符串传入（见 triage-loop.js 同款处理）。
const A = (typeof args === 'string')
  ? (() => { try { return JSON.parse(args) } catch { return {} } })()
  : (args || {})

const item = A.item
if (!item) throw new Error('process-item: args.item 必填')

// plan-driven TDD 步数上限（仅 feature 走 Planner 时生效；planner steps 超出则 trim）
// 防御性 sanitize：NaN / 0 / 负数 / Infinity 全部回退到 6；正常值 floor 防止小数
// 透传 kebab-case 兼容（与 triage-loop.js:91 行为对齐）
const maxSteps = (() => {
  const n = Number(A.maxSteps ?? A['max-steps'] ?? 6)
  return Number.isFinite(n) && n >= 1 ? Math.floor(n) : 6
})()

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
// 硬校验 baseSynced：dev 没同步到最新就切的 feature 分支会基于过期 dev，
// 破坏「每个 item 基于干净最新 dev」核心不变量（小模型常 git pull 失败却不写 error）。
// 早 fail 好过做完一堆工作才在 Merge 阶段撞 BEHIND/CONFLICTING——triage-loop 会 catch 并跳过本项继续。
if (!branchCheck.baseSynced) {
  throw new Error(
    `BranchCheck: dev 未同步到最新（baseSynced=false, fromBranch=${branchCheck.fromBranch ?? '?'}）——` +
    `拒绝基于过期 dev 切分支。多为 git pull 失败（网络/远端），重跑即可。`
  )
}
const workBranch = branchCheck.featureBranch || featureBranch

const kind = dispatchKind(item)
log(`#${item.id} 派单: ${kind} (${item.title})`)

// l3SplitInto 必须在 try 块**外**声明（catch 块访问不到 try 内的 let）——
// L3 createSubs 后赋值；catch 块检测非空 → 调补救 agent 给已建 sub-issue + 原 issue
// 加 `epic` 标签（让 issue-prioritizer 排除这些 orphan sub-issue，避免下轮 triage 重复派单）
let l3SplitInto = null

// 整个处理流程包在 try 里：单 item 失败返回 { ok:false } 让 triage-loop 跳过继续，
// 而不是 throw 崩溃整个循环（双保险——triage-loop 侧也有 try/catch）。
try {
  // planSteps：仅 feature 走 Planner 后才赋值；null = 走默认固定 2 步 TDD（bug/refactor/fix-*）
  let planSteps = null

  // stale / 简单 issue 不走完整流程
  if (kind === 'stale') {
    await agent(
      `判断 issue #${item.id} (${item.title}) 是否需要关闭或 ping 作者。` +
      `如果超过 30 天无活动且无进展评论，添加 stale label 并评论"如无更新将在 7 天后关闭"。`,
      { phase: 'Dispatch', agentType: 'issue-prioritizer' }
    )
    return { ok: true, prNumber: null, merged: false, followupCount: 0 }
  }

  // 1. Planner（仅 feature）—— 产出 PLAN_SCHEMA 结构化计划，供下游 L2/L3 分支决策
  //    ⚠️ 必须在 BDD 之前（对齐本文件顶部注释 + meta.phases 声明的 Planner→BDD 顺序）：
  //    feature 若判 decomposable=false 会在本块内提前 return 走 L3 拆子 issue，
  //    原 feature 分支随后被废弃。若 BDD 跑在前，它写的验收测试会随废弃分支一起丢掉
  //    （#74 实测：bdd-guide 白写 460 行 RED 测试）。且 Planner prompt 只用 item.body，
  //    不消费 BDD 产出——BDD 在前对 Planner 毫无价值。故先 Planner 定 L2/L3，
  //    确定要在本 PR 实现（L2 / 非 feature）才在下面跑 BDD。
  if (NEEDS_PLANNER_KINDS.has(kind)) {
    const plan = await agent(
      `为 feature #${item.id} "${item.title}" 设计结构化实现计划。\n\n` +
      `Issue body:\n${item.body}\n\n` +
      `**任务**：判断本 feature 能否用 plan-driven 多步 TDD 在一个 PR 内完成；不能则拆成子 issue。\n\n` +
      `**判定标准**：\n` +
      `- **decomposable=true**（L2 路径）：总改动 ≤ ${maxSteps * 5} 文件、改动**同质**（不跨多模块/不需架构决策/不需新增外部依赖）。\n` +
      `  返回 steps[]，每步 ≤5 文件、最多 ${maxSteps} 步；steps 超出上限**保留前 ${maxSteps} 步并在 reason 写明「trim 到上限」**。\n` +
      `- **decomposable=false**（L3 路径）：需跨多 PR / 架构决策 / 引入新依赖 / 跨多模块 / 每步 >5 文件且不同质。\n` +
      `  返回 subIssues[]，每项是独立可派单的工作单元；用 dependsOn[] 标依赖（序号 0-indexed 指向同数组内其它 sub-issue）。\n\n` +
      `**硬约束**：\n` +
      `- plan-driven 每步 ≤5 文件；steps 数组只列 ≤${maxSteps} 项\n` +
      `- 严格按 schema 返回（decomposable/reason 必填，steps/subIssues 二选一）\n` +
      `- reason 必须可解释判定依据（review 时核对）\n\n` +
      `**回报 schema**：\n` +
      `- decomposable: boolean\n` +
      `- reason: string（解释为什么这么判定）\n` +
      `- steps?: [{ title, description, files?: [string] }]（decomposable=true 时填）\n` +
      `- subIssues?: [{ title, description, dependsOn?: [number], labels?: [string] }]（decomposable=false 时填）`,
      { phase: 'Planner', agentType: 'planner', schema: PLAN_SCHEMA }
    )
    log(`#${item.id} planner: decomposable=${plan?.decomposable} reason=${plan?.reason?.slice(0, 100)}`)
    if (!plan || typeof plan.decomposable !== 'boolean') {
      throw new Error(`#${item.id} Planner 返回非法（缺少 decomposable 字段）：${JSON.stringify(plan)}`)
    }

    // L3：decomposable=false → 4 个独立 agent 串行：ensureLabels → createSubs → linkDeps → makeEpic
    // 严格成功判据：ensureLabels.ok && createSubs.createdCount≥1 && makeEpic.epicLabeled=true
    // 不满足任一条件 → throw 让外层 catch + triage-loop skipped++（不留半成品）
    if (!plan.decomposable) {
      const subIssues = Array.isArray(plan.subIssues) ? plan.subIssues : []
      if (subIssues.length === 0) {
        throw new Error(`#${item.id} Planner 判 L3 但 subIssues 为空——planner 必须给出可派单的子任务列表`)
      }
      log(`#${item.id} L3 拆分: ${subIssues.length} 个子 issue（4-agent 流水线）`)

      // Agent 1: ensureLabels —— 防护 followup-from / epic 标签不存在（CRITICAL #1）
      const labels = await agent(
        `为即将执行的 L3 issue 拆分准备 GitHub labels。\n\n` +
        `**目标**：确保仓库存在 \`followup-from\` 和 \`epic\` 两个 label；不存在则创建。\n\n` +
        `**操作**：\n` +
        `1. \`gh label list --json name --jq '.[].name'\` 拿仓库现有 labels\n` +
        `2. \`followup-from\` 不存在 → \`gh label create followup-from --description "子任务关联的 epic issue" --color "cccccc"\`\n` +
        `3. \`epic\` 不存在 → \`gh label create epic --description "L3 拆分出的 tracking epic 看板" --color "5319e7"\`\n` +
        `4. **不**创建已有 label（避免冲突）\n\n` +
        `**边缘 case（关键）**：\n` +
        `- \`gh label create <name>\` 在 label 已存在时返回 **exit code 1** + stderr \`already exists\` —— **这不是错误**\n` +
        `- 判定：若 exit code = 1 且 stderr 含 \`already exists\`（或 stdout/list 已确认存在）→ 该 label 已就位，照填 \`*: true\`\n` +
        `- 只在 exit code ≠ 0 且不是 "already exists" 时才算创建失败，填 \`*: false\` + errors\n\n` +
        `**严格回报 schema**（按字段填实际状态，不要乐观）：\n` +
        `- ok: boolean（所有必需 label 已就绪）\n` +
        `- followupFromExists: boolean（创建后或本来就存在为 true）\n` +
        `- epicExists: boolean（同上）\n` +
        `- errors: string[]（每条失败描述，不致命但要记）\n\n` +
        `**注意**：本 agent 失败时 throw——后续 createSubs 必然依赖 label 存在。`,
        { phase: 'Decompose to Epic', agentType: 'general-purpose', schema: L3_ENSURE_LABELS_SCHEMA }
      )
      log(`#${item.id} L3 ensureLabels: ok=${labels?.ok} followup=${labels?.followupFromExists} epic=${labels?.epicExists} errors=${labels?.errors?.length ?? 0}`)
      if (!labels?.ok) {
        throw new Error(`#${item.id} L3 ensureLabels 失败：${JSON.stringify(labels?.errors ?? [])}`)
      }

      // Agent 2: createSubs —— 建子 issue（核心；至少 1 个成功才继续）
      const createResult = await agent(
        `对 issue #${item.id}（原标题：${item.title}）执行 L3 子 issue 创建。\n\n` +
        `**planner 给的子任务列表**（subIssues，按执行顺序）：\n${JSON.stringify(subIssues, null, 2)}\n\n` +
        `**操作**：\n` +
        `对**每个** subIssue 调 \`mcp__github__create_issue\`：\n` +
        `- owner/repo: vs0533/10e0\n` +
        `- title: subIssue.title\n` +
        `- body: \`Part of #${item.id}\\n\\n\${subIssue.description}\\n\\n依赖：\${subIssue.dependsOn?.length ? '创建后回填实际 issue 号' : '无'}\\n\\n自动从 #${item.id} 拆分（triage L3）\`\n` +
        `- labels: [\`followup-from:#${item.id}\`, \`enhancement\`, ...(subIssue.labels || [])]\n\n` +
        `**严格统计**：\n` +
        `- createdCount: 实际创建成功的 issue 数\n` +
        `- splitInto: 实际创建的 issue number 数组，**严格按 subIssues 顺序**（失败的占 null 占位）\n` +
        `- errors: 失败描述数组（每个失败一条）\n\n` +
        `**关键**：返回 splitInto 数组**长度等于 subIssues.length**，失败的填 null——这样 linkDeps 步骤才能正确对齐序号。`,
        { phase: 'Decompose to Epic', agentType: 'general-purpose', schema: L3_CREATE_SUBS_SCHEMA }
      )
      log(`#${item.id} L3 createSubs: created=${createResult?.createdCount}/${subIssues.length} errors=${createResult?.errors?.length ?? 0}`)
      if ((createResult?.createdCount ?? 0) < 1) {
        throw new Error(`#${item.id} L3 createSubs 全部失败（createdCount=${createResult?.createdCount}）：${JSON.stringify(createResult?.errors ?? [])}——L3 拆分失败，留人工`)
      }
      // 记录到外层作用域，供 catch 块补救（避免 makeEpic/linkDeps 失败时 orphan sub-issue）
      l3SplitInto = createResult.splitInto

      // Agent 3: linkDeps —— 回填依赖（独立因为序号↔实际 issue 号转换易错）
      const linkResult = await agent(
        `对 L3 拆分的子 issue 回填依赖关系。\n\n` +
        `**原始 subIssues（含 dependsOn 序号）**：\n${JSON.stringify(subIssues, null, 2)}\n\n` +
        `**实际创建的子 issue（splitInto，按 subIssues 顺序，null = 该位置创建失败）**：\n${JSON.stringify(createResult.splitInto)}\n\n` +
        `**操作**：\n` +
        `对 splitInto 中**非 null 且有 dependsOn.length > 0** 的 issue，调 \`mcp__github__update_issue\`：\n` +
        `- owner/repo: vs0533/10e0\n` +
        `- issue_number: splitInto[i]\n` +
        `- body: 现有 body + \`\\n\\n**依赖**：#${createResult.splitInto.map(n => n ?? '?').join(', #')}\`（把依赖序号 → 实际号；null 占位变 ?）\n\n` +
        `**关键**：用 \`mcp__github__get_issue\` 拿现有 body 再追加，不要直接覆盖（保留 issue 原有内容）。\n\n` +
        `**严格回报**：\n` +
        `- linked: 成功回填依赖的 issue 数\n` +
        `- errors: 失败描述数组`,
        { phase: 'Decompose to Epic', agentType: 'general-purpose', schema: L3_LINK_DEPS_SCHEMA }
      )
      log(`#${item.id} L3 linkDeps: linked=${linkResult?.linked} errors=${linkResult?.errors?.length ?? 0}`)
      // linkDeps 失败 swallow（依赖回填是 nice-to-have，不阻塞 L3 主结果）

      // Agent 4: makeEpic —— 原 issue 转 tracking epic（关键不变量：epicLabeled=true 才算 L3 成功）
      const epicResult = await agent(
        `把原 issue #${item.id}（${item.title}）转 tracking epic。\n\n` +
        `**子 issue 创建结果**（用于填 checklist body）：\n${JSON.stringify(createResult.splitInto)}\n\n` +
        `**操作**（按顺序，每步单独调 MCP）：\n` +
        `1. \`mcp__github__get_issue\` 拿 issue #${item.id} 的**现有 labels**（保留用户原有 label 列表；body 不在此取，用下方 template 内嵌的 \`item.body\`）\n` +
        `2. \`mcp__github__update_issue\` 加 \`epic\` 标签：labels = 现有 labels + [\`epic\`]（**去重合并**，不要覆盖）\n` +
        `3. \`mcp__github__update_issue\` 改 body 为 checklist（**完全替换**，原内容已拼到"## 原始需求"段）：\n\n` +
        `\`\`\`\n# Tracking Epic for #${item.id}\n\n${item.title}\n\n（本 issue 由 triage L3 自动拆分为 ${subIssues.length} 个子任务；本身不含可直接实现的工作，子 issue 全部关闭后人工关闭本 epic）\n\n` +
        `## 原始需求\n\n${item.body && item.body.trim() ? item.body.trim() : '（原 issue body 为空）'}\n\n` +
        `## 子任务进度\n${subIssues.map((si, i) => `- [ ] ${createResult.splitInto[i] ? '#' + createResult.splitInto[i] : '#? (创建失败)'} ${si.title}`).join('\\n')}\n\`\`\`\n\n` +
        `4. **不**修改 state（保持 open 作 epic 看板）\n\n` +
        `**关于原 body 的快照说明**：上述 template 用 \`item.body\`（process-item 入口传入的 RANK 快照），\n` +
        `而非重新 fetch GitHub 实时 body——既避免额外 API 调用，又保证 checklist 反映"拆 L3 那一刻的需求"。\n` +
        `事后人工修改原 issue body 不会自动同步到 epic（如需更新请人工编辑本 issue）。\n\n` +
        `**严格回报 schema**（关键不变量 epicLabeled）：\n` +
        `- epicLabeled: boolean（**必须 true**；false = L3 失败）\n` +
        `- bodyRewritten: boolean（失败 swallow）\n` +
        `- errors: 失败描述数组`,
        { phase: 'Decompose to Epic', agentType: 'general-purpose', schema: L3_MAKE_EPIC_SCHEMA }
      )
      log(`#${item.id} L3 makeEpic: epicLabeled=${epicResult?.epicLabeled} bodyRewritten=${epicResult?.bodyRewritten} errors=${epicResult?.errors?.length ?? 0}`)
      if (!epicResult?.epicLabeled) {
        throw new Error(`#${item.id} L3 makeEpic 关键不变量失败（epicLabeled=false）：${JSON.stringify(epicResult?.errors ?? [])}——L3 拆分失败，留人工`)
      }

      // 全部通过 → 返回 decomposed:true
      return {
        ok: true,
        prNumber: null,
        prUrl: null,
        merged: false,
        decomposed: true,
        splitInto: createResult.splitInto.filter(n => n != null),
        followupCount: 0,
        fixedCount: 0,
        botVerdict: 'NONE',
        summary: `L3 拆分为 ${createResult.createdCount}/${subIssues.length} 个子 issue（planner reason: ${plan.reason}；linkDeps: ${linkResult?.linked ?? 0}/${createResult.createdCount}；epic OK）`,
      }
    }

    // L2：decomposable=true → 准备 planSteps 给下游 TDD 循环
    // 边界 case：steps 为空数组 / 全 falsy / 超过 maxSteps → 容错
    const rawSteps = Array.isArray(plan.steps) ? plan.steps : []
    const trimmed = rawSteps.slice(0, maxSteps).filter(s => s && s.title)
    if (rawSteps.length === 0) {
      log(`#${item.id} planner 返回 decomposable=true 但 steps 为空，降级走默认固定 2 步 TDD`)
    } else if (trimmed.length === 0) {
      // 步骤数 >0 但 filter 后全 falsy（planner 返回了空 step 对象等异常）→ 降级
      log(`#${item.id} planner steps=${rawSteps.length} 但 filter 后为空，降级走默认固定 2 步 TDD`)
    } else {
      planSteps = trimmed
      if (rawSteps.length > maxSteps) {
        log(`#${item.id} planner steps=${rawSteps.length} 超过 maxSteps=${maxSteps}，trim 到 ${planSteps.length}`)
      }
    }
  }

  // 2. BDD（bug + feature-L2 走；refactor/docs/fix-ci/fix-review/stale/default 跳过）
  //    放在 Planner 之后：feature-L3 已在上面 return，不会跑到这里——避免 L3 浪费 BDD 测试。
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

  // 3. TDD —— feature 有结构化 plan 时走 plan-driven 多步（每步 <5 文件，总量不限，解决"改动大但同质"）；
  //    其它（bug/refactor/fix-*）走默认固定 2 步（schema→impl）。每步独立 agent 接力同一 workspace，
  //    最终 build/test/format 门禁由下面 Tests 阶段统一兜底。
  if (planSteps) {
    log(`#${item.id} plan-driven TDD: ${planSteps.length} 步`)
    for (let s = 0; s < planSteps.length; s++) {
      const step = planSteps[s]
      const files = Array.isArray(step.files) ? step.files : []
      await agent(
        `Plan-driven TDD Step ${s + 1}/${planSteps.length}: ${step.title}\n\n` +
        `feature #${item.id} "${item.title}"\n\n` +
        `**本步范围**：${step.description}\n` +
        `**只许改这些文件**（plan 指定，≤5）：\n${files.length ? files.map(f => `- ${f}`).join('\n') : '（plan 未列具体文件，按描述做最小改动）'}\n\n` +
        `**硬约束**：\n` +
        `- 严格限定本步范围，不做后续步骤的事（靠接力）\n` +
        `- 改动 ≤5 文件；若本步实际需 >5 文件，说明 plan 拆得不够细，回报 { needsResplit: true, reason } 并退出\n` +
        `- 用 Read 看前面步骤已就位的代码/接口/RED 测试\n` +
        `- 本步收尾 \`DOTNET_CLI_UI_LANGUAGE=en-US dotnet build 10e0.slnx 2>&1 | tail -10\` 应通过（已有 RED 测试无所谓）\n` +
        `- 回报：{ filesChanged, buildOk, note } 结构化数据`,
        { phase: 'TDD-Impl', agentType: 'tdd-guide' }
      )
    }
  } else {
    // 默认固定 2 步（bug/refactor/fix-ci/fix-review）：
    //   1. schema/接口（DB 模型、新接口签名、DI 注册）—— 改动 < 5 文件
    //   2. 实现（handler/service/evaluator）+ 补边界测试 —— 改动 < 5 文件
    // 关于 needsDownstream：小 issue（无新 schema）Step 1 返回 { needsDownstream:true } 是正常流，Step 2 接力。
    await agent(
      `TDD Step 1/2 (schema & interfaces) for #${item.id} "${item.title}"\n\n` +
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
      `TDD Step 2/2 (implementation) for #${item.id} "${item.title}"\n\n` +
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
  }

  // 4. tests (红就 fail) — schema 验证，process-item 直接看 failed 字段，不依赖正则
  //    （这一步已是唯一的 build/test/format 门禁，原 TDD-Verify 因与本步重复且结果无人消费已删）
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
    `3. **format 先自动修复再验证**（空白/换行不该卡门禁——小模型常漏末尾换行，自动补即可）：\n` +
    `   a. \`DOTNET_CLI_UI_LANGUAGE=en-US dotnet format 10e0.slnx --severity warn 2>&1 | tail -5\`（**不带 --verify**，就地补换行/空白；改动留工作区，Open PR 阶段 commit 会带上）\n` +
    `   b. \`DOTNET_CLI_UI_LANGUAGE=en-US dotnet format 10e0.slnx --verify-no-changes --severity warn 2>&1 | tail -5\` → formatOk = exit code 0（a 修过后这里应必过）\n\n` +
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
  const reviewTimeoutMs = Number(A.reviewTimeoutMs ?? 900000) || 900000  // 默认 15 分钟
  const maxReviewRounds = Number(A.maxReviewRounds ?? 3) || 3
  const pollCount = Math.max(3, Math.floor(reviewTimeoutMs / 20000))  // shell seq 控制轮询次数，间隔 20s
  const REVIEW_SCHEMA = {
    type: 'object',
    required: ['items', 'botVerdict', 'ciSettled'],
    properties: {
      // claude-review.yml bot 的结论：APPROVE / REQUEST_CHANGES（有 🔴 Critical）/ NONE（没找到 bot 评论）
      botVerdict: { type: 'string', enum: ['APPROVE', 'REQUEST_CHANGES', 'NONE'] },
      // PR 与 dev 的可合并状态（用于冲突门禁，防自愈循环对着 merge 冲突空转）
      mergeable: { type: 'string' },         // MERGEABLE / CONFLICTING / UNKNOWN
      mergeStateStatus: { type: 'string' },  // CLEAN / DIRTY / BLOCKED / BEHIND / UNSTABLE / UNKNOWN
      // CI 是否真的全绿可合（**门禁硬指标**：bucket 全部 pass/skipping/cancel + mergeStateStatus=CLEAN）
      // 修复（2026-06-21）：原版只解析 bot 评论判 verdict，导致 CI 还在 IN_PROGRESS 时
      // 旧 bot 评论里的 "VERDICT: APPROVE" 被误用作合并依据；Merge 阶段靠兜底拒合救回，
      // 但 phase 显示混乱。现强制 ciSettled 必填，未绿则 verdict 强制 NONE + 不调 Merge，
      // 留到下一轮 triage 自然重试。
      ciSettled: { type: 'boolean' },
      // ciSettled=false 时填：哪几个 check 还 pending/fail，或 mergeStateStatus 是哪个非 CLEAN 值
      checksNotSettled: { type: 'string' },
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
      `**🔴 硬门禁（违反任一就立刻停，不准返回 APPROVE）**：\n` +
      `- **所有 check 必须 COMPLETED 且 bucket 不含 fail**：\n` +
      `  - bucket ∈ {pass, skipping, cancel} 算 OK\n` +
      `  - bucket = pending → 还在跑\n` +
      `  - bucket = fail → 已失败\n` +
      `  - 任何 pending/fail → **立刻返回 ciSettled=false + botVerdict="NONE" + checksNotSettled 写明具体哪些 check pending/fail**，**不要**返回 APPROVE\n` +
      `- **mergeStateStatus 必须 CLEAN**（PR 处于"可干净合并"状态）：\n` +
      `  - CLEAN = 全绿可合\n` +
      `  - UNSTABLE = 还有 check 跑 → 视为未 settled\n` +
      `  - DIRTY / BLOCKED / BEHIND / UNKNOWN = 异常状态 → 视为未 settled\n` +
      `  - **不满足 → 立刻返回 ciSettled=false + botVerdict="NONE" + checksNotSettled 写"mergeStateStatus=XXX"**\n\n` +
      `**第二、三、四步只在 ciSettled=true 时才执行**：\n\n` +
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
      `**第四步：解析最新 bot VERDICT**（bot 评论的厂商无关特征：HTML marker "<!-- triage-review-bot -->" 或 "🤖 Automated Code Review" header 定位是 bot 评论；正文 "VERDICT:" / "Verdict:" 行作最终结论。**别靠具体模型名认 bot**，后端模型可换）：\n` +
      `- 含 "VERDICT: REQUEST_CHANGES" / "Verdict: **REQUEST_CHANGES**" → "REQUEST_CHANGES"\n` +
      `- 含 "VERDICT: APPROVE" / "Verdict: **APPROVE**" → "APPROVE"（**仅在 ciSettled=true 时才返回**；ciSettled=false 时已提前 return NONE）\n` +
      `- 找不到 bot 评论 → "NONE"\n\n` +
      `**CI 状态总结（必填，写入 schema）**：\n` +
      `- 所有 check bucket ∈ {pass, skipping, cancel} + mergeStateStatus=CLEAN → ciSettled=true\n` +
      `- 其他情况 → ciSettled=false + checksNotSettled 写明具体未 settled 的项（如 "Build & Test pending"/"mergeStateStatus=UNSTABLE"）\n\n` +
      `返回 schema: { botVerdict, mergeable, mergeStateStatus, ciSettled, checksNotSettled?: string, items: [{ id, user, body, path?, line?, state }] }（数组放 items 字段下）。无评论返回 { botVerdict:"NONE", items: [], mergeable, mergeStateStatus, ciSettled, checksNotSettled }。`,
      { phase: 'Watch Review', schema: REVIEW_SCHEMA, agentType: 'general-purpose' }
    )
    const reviews = reviewResult?.items ?? []
    const botVerdict = reviewResult?.botVerdict ?? 'NONE'
    const ciSettled = reviewResult?.ciSettled ?? false
    finalVerdict = botVerdict
    log(`PR #${prNumber} 轮 ${round}: ${reviews.length} 条评论, verdict=${botVerdict}, ciSettled=${ciSettled}, mergeable=${reviewResult?.mergeable ?? '?'}/${reviewResult?.mergeStateStatus ?? '?'}`)

    // (1.5) CI settled 硬门禁：bucket 还有 pending/fail 或 mergeStateStatus != CLEAN → 不调 Merge
    //   修复（2026-06-21）：原版只看 bot 旧评论里的 "VERDICT: APPROVE" 就走合并路径，
    //   忽略了 CI 实际还在 IN_PROGRESS；Merge agent 靠兜底拒合救回，但 phase 显示混乱。
    //   现强制：ciSettled=false → 立刻 break，verdict 仅供记录，不触发合并，留到下一轮 triage 重试。
    //   防御深度：即使 prompt 漏校验，workflow 侧也兜底再 check 一次 ciSettled 字段。
    if (!ciSettled) {
      mergeReason = `PR #${prNumber} CI 未全绿（${reviewResult?.checksNotSettled ?? 'check pending/fail or mergeStateStatus!=CLEAN'}）——bot verdict=${botVerdict} 仅供记录，不触发合并，留到下一轮 triage 重试`
      log(`PR #${prNumber} CI 未 settled: ${mergeReason}`)
      break
    }

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
  // 单 item 失败：不崩溃整个 triage-loop，返回 ok:false 让上层处理。
  const msg = e?.message ?? String(e)
  log(`#${item.id} 处理失败（已捕获）: ${msg}`)

  // 失败改动保护：把工作区已有改动 commit + push 到 feature 分支（WIP），
  // 否则下一轮 BranchCheck 的 `git checkout -f dev` 会丢掉它们——
  // 本次 #49 案例就是代码全写完、751 测试全过，只因 format 卡，改动差点被丢。
  // 内层 try/catch 包住：保护本身失败也不让异常逃出 process-item。
  let savedBranch = null
  try {
    const saveResult = await agent(
      `process-item 处理 #${item.id} 失败了，但工作区可能有有价值的改动（如代码已写完只是某步卡住）。\n` +
      `**抢救改动，别让它被下一轮 \`git checkout -f dev\` 丢掉：**\n` +
      `1. \`git status --porcelain\`——没有任何改动就返回 { saved:false, reason:"无改动" }\n` +
      `2. 有改动 → 确认当前在 feature 分支 \`${workBranch}\`（不是 dev/main）；若不在则返回 { saved:false, reason:"不在 feature 分支" }\n` +
      `3. \`git add -A\` → \`git commit -m "wip: #${item.id} 处理中断保存"\`\n` +
      `4. \`git push -u origin ${workBranch}\`（推远端彻底安全；push 失败也没关系，本地 commit 已保住）\n` +
      `回报 schema：{ saved: boolean, branch?: string, reason?: string }`,
      { phase: 'BranchCheck', agentType: 'general-purpose', schema: {
        type: 'object', required: ['saved'],
        properties: { saved: { type: 'boolean' }, branch: { type: 'string' }, reason: { type: 'string' } },
      } }
    )
    if (saveResult?.saved) savedBranch = saveResult.branch || workBranch
    log(`#${item.id} 失败改动保护: ${JSON.stringify(saveResult)}`)
  } catch (se) {
    log(`#${item.id} 改动保护也失败（忽略）: ${se?.message ?? String(se)}`)
  }

  // H1 补救：L3 失败时若 createSubs 已建了 sub-issue（l3SplitInto 非 null），
  // 调补救 agent 给原 issue + 所有已建 sub-issue 加 `epic` 标签，让 issue-prioritizer 排除，
  // 避免下轮 triage 把这些 orphan sub-issue 当新派单对象处理（重复处理会与原 feature 冲突）。
  if (Array.isArray(l3SplitInto) && l3SplitInto.length > 0) {
    const validSubs = l3SplitInto.filter(n => n != null)
    log(`#${item.id} L3 失败补救: ${validSubs.length} 个 orphan sub-issue + 原 issue 加 epic 标签`)
    try {
      await agent(
        `L3 拆分失败后的补救——避免 orphan sub-issue 被下轮 triage 重复派单。\n\n` +
        `**原 issue** #${item.id}（${item.title}）\n` +
        `**已建 sub-issue 列表**（splitInto，按 planner 顺序，null = 该位置创建失败）：\n${JSON.stringify(l3SplitInto)}\n\n` +
        `**操作**：\n` +
        `1. 给**原 issue** #${item.id} 加 \`epic\` 标签：用 \`mcp__github__get_issue\` 拿现有 labels → 合并 ['epic'] 去重 → \`mcp__github__update_issue\`\n` +
        `2. 给每个 splitInto 中**非 null 的 sub-issue** 也加 \`epic\` 标签（同样 get→merge→update 流程，避免覆盖原 labels）\n` +
        `3. **不**改任何 body（保留原内容；补救只防重复派单，不重做 epic 化）\n` +
        `4. **不**修改 state（保持 open，等人工处理）\n` +
        `5. 给原 issue 发一个 comment 说明："⚠️ triage L3 拆分失败，本 issue 已标 epic 但未做 checklist 改造。` +
        `已建 sub-issue ${validSubs.length} 个也标 epic 防重复派单。请人工检查并决定后续处理。"\n\n` +
        `**严格回报 schema**：\n` +
        `- ok: boolean（原 issue 加 epic 成功 + 至少 1 个 sub-issue 加 epic 成功）\n` +
        `- subIssueLabeledCount: number（成功加 epic 的 sub-issue 数）\n` +
        `- errors: string[]（失败描述）\n\n` +
        `**关键**：即使补救失败，原 try 块仍会返回 ok:false + error（本补救 swallow 异常，避免二次失败掩盖原错误）`,
        { phase: 'Decompose to Epic', agentType: 'general-purpose', schema: L3_REMEDIATE_SCHEMA }
      )
    } catch (re) {
      log(`#${item.id} L3 补救失败（已忽略，不掩盖原错误）: ${re?.message ?? re}`)
    }
  }

  log(`#${item.id} 最终: ok=false, savedBranch=${savedBranch ?? 'none'}`)
  return { ok: false, prNumber: null, merged: false, followupCount: 0, error: msg, savedBranch }
}
