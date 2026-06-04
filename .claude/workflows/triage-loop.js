// 外层循环：拉取 issue-prioritizer 排序结果 → 过滤 → 派给 process-item 子工作流
// 设计：while 循环由用户通过 --max 控制；每轮重新拉排序（前项可能改变优先级）
// worktree 策略：外层不强制 isolation，让 process-item 内部按"接力"模式自管
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
}

log(`opts: ${JSON.stringify(opts)}`)

let processed = 0
let skipped = 0

while (processed + skipped < opts.max) {
  // 强制结构化输出：agent 若返回非法 JSON，schema 校验会重试
  const rankedResult = await agent(
    '返回当前仓库未处理 issue/PR 的优先级排序（按 bug > failing-ci > review-feedback > feature > refactor > docs > stale）。' +
    '每项必须含 id, type(issue|pr), url, title, body, labels(array), priority(number)。' +
    '排除已有关联 PR 的 issue、已 close 的项、stale > 30 天且无活动的项。' +
    '请把数组放在 items 字段下返回（schema 要求）。',
    { phase: 'Rank', schema: RANK_SCHEMA, agentType: 'issue-prioritizer' }
  )
  const ranked = rankedResult?.items ?? []

  if (ranked.length === 0) {
    log(`队列已清空，共处理 ${processed} 项、跳过 ${skipped} 项。`)
    break
  }

  const filtered = ranked.filter(it => applyFilters(it, opts))
  if (filtered.length === 0) {
    log('过滤后无匹配项，退出。')
    break
  }

  const item = filtered[0]
  log(`轮 ${processed + skipped + 1}/${opts.max}: #${item.id} ${item.title} (${item.labels.join(',') || 'no-label'})`)

  if (opts.dryRun) {
    log(`  [dry-run] 跳过实际处理`)
    skipped++
    continue
  }

  // 调子工作流；子工作流内部按"外层 worktree, 内层接力"自管文件状态
  const result = await workflow('process-item', { item, followupFromPr: null })

  if (result && result.ok) {
    log(`  ✓ #${item.id} 完成: pr=#${result.prNumber}, followup=${result.followupCount ?? 0}`)
    processed++
  } else {
    log(`  ✗ #${item.id} 失败: ${result?.error ?? 'unknown'}`)
    skipped++
  }
}

log(`triage-loop 结束: processed=${processed}, skipped=${skipped}`)
