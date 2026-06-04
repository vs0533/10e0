// 派单策略：根据 item 的 labels + 标题决定走哪种处理路径
// 被 process-item.js 引用；未来其它工作流（如 nightly-triage、stale-cleanup）也可复用
//
// 输入：item = { id, type, url, title, body, labels: string[], priority }
// 输出：kind string ——
//   'stale'      只评论/关闭，不开发
//   'fix-ci'     修 CI，跳 BDD
//   'fix-review' 处理 review 评论，跳 BDD
//   'bug'        完整 7 步（BDD→TDD→...）
//   'feature'    完整 7 步 + 前置 planner
//   'refactor'   跳 BDD+planner
//   'docs'       跳 BDD，让 doc-updater 写
//   'default'    兜底，让 general-purpose 处理

const STALE_LABELS = ['stale']
const FAILING_CI_LABELS = ['failing-ci', 'ci-failing']
const REVIEW_FEEDBACK_LABELS = ['review-feedback', 'changes-requested']
const BUG_LABELS = ['bug', 'p0', 'p1', 'critical']
const FEATURE_LABELS = ['feature', 'enhancement']
const REFACTOR_LABELS = ['refactor', 'tech-debt', 'dead-code']
const DOCS_LABELS = ['docs', 'documentation']

const BUG_TITLE_REGEX = /fix|bug|error|exception|crash/i

export function dispatchKind(item) {
  const labels = item.labels || []

  if (labels.some(l => STALE_LABELS.includes(l))) return 'stale'
  if (labels.some(l => FAILING_CI_LABELS.includes(l))) return 'fix-ci'
  if (labels.some(l => REVIEW_FEEDBACK_LABELS.includes(l))) return 'fix-review'

  if (labels.some(l => BUG_LABELS.includes(l)) || BUG_TITLE_REGEX.test(item.title || '')) {
    return 'bug'
  }
  if (labels.some(l => FEATURE_LABELS.includes(l))) return 'feature'
  if (labels.some(l => REFACTOR_LABELS.includes(l))) return 'refactor'
  if (labels.some(l => DOCS_LABELS.includes(l))) return 'docs'

  return 'default'
}

// 哪些 kind 跳过 BDD 步（顺序按 dispatchKind 优先级）
export const SKIP_BDD_KINDS = new Set(['refactor', 'docs', 'fix-ci', 'fix-review', 'stale', 'default'])

// 哪些 kind 需要前置 planner（feature 类型）
export const NEEDS_PLANNER_KINDS = new Set(['feature'])

// 哪些 kind 走完整 7 步
export const FULL_PIPELINE_KINDS = new Set(['bug', 'feature'])

export const ALL_KINDS = [
  'stale', 'fix-ci', 'fix-review', 'bug', 'feature', 'refactor', 'docs', 'default',
]
