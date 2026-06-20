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

// 注意：不能直接用顶层 type:'array'，Qwen 子代理调用 StructuredOutput 时
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
  '排除已有关联 PR 的 issue、已 close 的项、stale > 30 天且无活动的项。' +
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
const opts = {
  max: args.max ?? 3,
  issuesOnly: Boolean(args.issuesOnly ?? args['issues-only']),
  prsOnly: Boolean(args.prsOnly ?? args['prs-only']),
  labels: parseLabels(args.labels),
  dryRun: Boolean(args.dryRun ?? args['dry-run']),
  // 默认：item 没成功合并到 dev 就停止整个循环（用户要求「必须合并+同步 dev 才能跑下一个」）。
  // 传 --continue-on-unmerged 才在未合并/失败时跳过该项继续下一个（批量吞吐模式）。
  continueOnUnmerged: Boolean(args.continueOnUnmerged ?? args['continue-on-unmerged']),
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
    result = await workflow('process-item', { item, followupFromPr: null })
  } catch (e) {
    log(`  ✗ #${item.id} 抛错（已捕获，跳过）: ${e?.message ?? String(e)}`)
    skipped++
    continue
  }

  // 只有「成功合并到 dev」才算真正完成、才继续下一个 item。
  if (result && result.ok && result.merged) {
    log(`  ✓ #${item.id} 已合并到 dev: pr=#${result.prNumber ?? 'n/a'}, followup=${result.followupCount ?? 0}`)
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
