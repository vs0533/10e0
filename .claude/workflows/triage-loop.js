// 外层循环：拉取 issue-prioritizer 排序结果 → 过滤 → 派给 process-item 子工作流
// 设计：while 循环由用户通过 --max 控制；每轮重新拉排序（前项可能改变优先级）
// worktree 策略：外层**不带** isolation——让 process-item 顺序接力同一 workspace，
//   每个 item 在 process-item 的 BranchCheck 阶段强制回到干净 dev 再切 feature 分支做隔离。
//
// 触发：用户说"批量处理 issue/PR"、"triage 循环"、"清空积压"等
// 入口：Workflow({ name: "triage-loop", args: { max: 20, issuesOnly: false, ... } })

export const meta = {
  name: 'triage-loop',
  description: '循环分诊：拉取 issue-prioritizer 排序 → 过滤 → 派给 process-item 子工作流处理每项',
  phases: [
    { title: 'Rank' },
    { title: 'Process Item' },
  ],
}

// 单 item 的结构化 schema（强制 issue-prioritizer 返回此格式）
const ITEM_SCHEMA = {
  type: 'object',
  required: ['id', 'type', 'url', 'title', 'body', 'labels', 'priority'],
  properties: {
    id: { type: 'number' },
    type: { type: 'string', enum: ['issue', 'pr'] },
    url: { type: 'string' },
    title: { type: 'string' },
    body: { type: 'string' },
    labels: { type: 'array', items: { type: 'string' } },
    priority: { type: 'number' },
  },
}

// 注意：不能直接用顶层 type:'array'，子代理调用 StructuredOutput 时
// 用的是命名对象参数（id/type/url...），而校验器要求 input 整体就是数组。
// 标准修复：把数组包到 items 字段里，下游 unwrap。
const RANK_SCHEMA = {
  type: 'object',
  required: ['items'],
  properties: {
    items: {
      type: 'array',
      items: ITEM_SCHEMA,
    },
  },
}

const RANK_PROMPT =
  '返回当前仓库未处理 issue/PR 的优先级排序（按 bug > failing-ci > review-feedback > feature > refactor > docs > stale）。' +
  '每项必须含 id, type(issue|pr), url, title, body, labels(array), priority(number)。' +
  '排除已有关联 PR 的 issue、已 close 的项、stale > 30 天且无活动的项、带 epic 标签的 tracking issue（它只是 L3 拆分出的子任务进度看板，不直接派单处理）。' +
  '请把数组放在 items 字段下返回（schema 要求）。'

// 把 labels 字符串转成数组（CLI 传过来是字符串）
function parseLabels(s) {
  if (!s) return null
  return String(s).split(',').map(x => x.trim()).filter(Boolean)
}

function applyFilters(item, opts) {
  if (opts.issuesOnly && item.type !== 'issue') return false
  if (opts.prsOnly && item.type !== 'pr') return false
  if (opts.labels && !item.labels.some(l => opts.labels.includes(l))) return false
  return true
}

phase('Rank')

// args 命名约定容错：harness 可能在 kebab-case 与 camelCase 间转换。
// 优先 camelCase（CLAUDE.md / skill 文档约定），fallback 到 kebab-case。
// args 容错：harness 有时把整个 args 序列化成 JSON 字符串传入（实测 `/triage --max 2`
// 收到 args="{\"max\":2}" 字符串 → args.max=undefined → 走默认 3 → 多跑一个 item）。
// 先规范化成对象，再读字段。
const A = (typeof args === 'string')
  ? (() => { try { return JSON.parse(args) } catch { return {} } })()
  : (args || {})

const opts = {
  // max 强制转数字（args 序列化还会把数字变字符串 "2"，见 triage-workflow-runtime-facts）
  max: Number(A.max ?? 3) || 3,
  issuesOnly: Boolean(A.issuesOnly ?? A['issues-only']),
  prsOnly: Boolean(A.prsOnly ?? A['prs-only']),
  labels: parseLabels(A.labels),
  dryRun: Boolean(A.dryRun ?? A['dry-run']),
  // 默认：item 没成功合并到 dev 就停止整个循环（用户要求「必须合并+同步 dev 才能跑下一个」）。
  // 传 --continue-on-unmerged 才在未合并/失败时跳过该项继续下一个（批量吞吐模式）。
  continueOnUnmerged: Boolean(A.continueOnUnmerged ?? A['continue-on-unmerged']),
  // plan-driven TDD 步数上限（仅 feature 走 Planner 时生效）；透传给 process-item。
  // 默认 6；用户传 --max-steps 8 → process-item 的 A.maxSteps = 8
  // 防御性 sanitize：NaN / 0 / 负数 / Infinity 全部回退到 6；正常值 floor 防止小数
  // 与 process-item.js:186-187 行为对齐（两边共用同一 sanitize 规则）
  maxSteps: (() => {
    const n = Number(A.maxSteps ?? A['max-steps'] ?? 6)
    return Number.isFinite(n) && n >= 1 ? Math.floor(n) : 6
  })(),
}

log(`opts: ${JSON.stringify(opts)}`)

// —— dry-run：只 rank 一次，列出整个队列后结束（不进循环，避免重复 rank 同一项 max 次）
if (opts.dryRun) {
  const rankedResult = await agent(RANK_PROMPT, { phase: 'Rank', schema: RANK_SCHEMA, agentType: 'issue-prioritizer' })
  const ranked = rankedResult?.items ?? []
  const filtered = ranked.filter(it => applyFilters(it, opts))
  log(`[dry-run] 过滤后队列 ${filtered.length} 项（max=${opts.max}），下面是将依次派单的项：`)
  for (const it of filtered.slice(0, opts.max)) {
    log(`  #${it.id} [${it.type}] ${it.title} (${(it.labels || []).join(',') || 'no-label'}) prio=${it.priority}`)
  }
  log(`[dry-run] 结束，未实际派单。去掉 --dry-run 即开始处理。`)
  return { processed: 0, skipped: filtered.length, dryRun: true }
}

let processed = 0
let skipped = 0
// 会话内去重：即使 issue-prioritizer 漏排已处理项，也不重复派单同一 id，避免空转/重复开 PR
const seen = new Set()

while (processed + skipped < opts.max) {
  // 强制结构化输出：agent 若返回非法 JSON，schema 校验会重试
  const rankedResult = await agent(RANK_PROMPT, { phase: 'Rank', schema: RANK_SCHEMA, agentType: 'issue-prioritizer' })
  const ranked = rankedResult?.items ?? []

  if (ranked.length === 0) {
    log(`队列已清空，共处理 ${processed} 项、跳过 ${skipped} 项。`)
    break
  }

  const filtered = ranked.filter(it => applyFilters(it, opts) && !seen.has(it.id))
  if (filtered.length === 0) {
    log('过滤后无新匹配项（可能都已在本轮会话处理过），退出。')
    break
  }

  const item = filtered[0]
  seen.add(item.id)
  log(`轮 ${processed + skipped + 1}/${opts.max}: #${item.id} ${item.title} (${item.labels.join(',') || 'no-label'})`)

  // try/catch 兜底：单个 item 在 process-item 内 throw（如 BranchCheck 致命错误）时
  // 跳过该项继续下一轮，而不是让整个 triage-loop 崩溃。
  let result
  try {
    result = await workflow('process-item', { item, followupFromPr: null, maxSteps: opts.maxSteps })
  } catch (e) {
    log(`  ✗ #${item.id} 抛错（已捕获，跳过）: ${e?.message ?? String(e)}`)
    skipped++
    continue
  }

  // 「成功合并到 dev」或「L3 拆分成功」才算真正完成、才继续下一个 item。
  if (result && result.ok && result.merged) {
    log(`  ✓ #${item.id} 已合并到 dev: pr=#${result.prNumber ?? 'n/a'}, followup=${result.followupCount ?? 0}`)
    if (result.summary) log(`    summary: ${result.summary}`)
    processed++
  } else if (result && result.ok && result.decomposed) {
    // L3：大 feature 已展开为子 issue + 原 issue 转 tracking epic —— 妥善处理，继续下一个
    log(`  🧩 #${item.id} 已拆分为 ${result.splitInto?.length ?? 0} 个子 issue（原 issue 转 tracking epic），继续`)
    // L3 summary 含 planner reason / linkDeps 数 / epic 状态，便于事后审计
    if (result.summary) log(`    summary: ${result.summary}`)
    processed++
  } else {
    // 未合并（留人工/REQUEST_CHANGES/NONE/冲突）或失败 —— 默认停止整个循环，等人工处理
    const why = !result?.ok
      ? `失败: ${result?.error ?? 'unknown'}${result?.savedBranch ? `（改动已存 ${result.savedBranch}）` : ''}`
      : `处理完但未合并: ${result?.mergeReason ?? '留人工'}（pr=#${result?.prNumber ?? 'n/a'}）`
    skipped++
    if (opts.continueOnUnmerged) {
      log(`  ⏭ #${item.id} ${why} —— --continue-on-unmerged 已开，跳过该项继续下一个`)
    } else {
      log(`  ⏸ #${item.id} ${why}`)
      log(`未成功合并到 dev，按默认停止循环（要无视未合并继续下一个，加 --continue-on-unmerged）。processed=${processed}, skipped=${skipped}`)
      break
    }
  }
}

log(`triage-loop 结束: processed=${processed}, skipped=${skipped}`)
