// 盯 PR 自动 review：轮询 GitHub Actions claude-review.yml 状态，
// 完成后用 mcp__github__get_pull_request_reviews 拉取所有 review 评论。
//
// 设计：把"等待 + 拉取"压在一个 agent 调用里（agent 内部用 while 轮询），
//      避免在脚本顶层 while 烧 budget。每 item 只花 1 个 agent。
//
// 输入：args.prNumber, args.prUrl, args.timeoutMs (默认 900000 = 15min)
// 输出：[{ id, user, body, path?, line?, state }]

export const meta = {
  name: 'wait-for-pr-review',
  description: '等 PR 自动 review 跑完，返回所有 review 评论',
  phases: [{ title: 'Poll' }, { title: 'Fetch' }],
}

const prNumber = args.prNumber
const prUrl = args.prUrl
const timeoutMs = args.timeoutMs ?? 900000

if (!prNumber || !prUrl) {
  throw new Error('wait-for-pr-review: args.prNumber 与 args.prUrl 必填')
}

// 顶层 type:'array' 在 Qwen 子代理上会触发 "root: must be array" 校验错
// （代理用命名对象参数调用 StructuredOutput，校验器要求 input 整体是数组）。
// 修复：包到 items 字段，下游 unwrap。
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

// 一步到位：让 agent 自己轮询
const result = await agent(
  `盯 PR #${prNumber} (${prUrl}) 的 .github/workflows/claude-review.yml 自动 review。\n\n` +
  `步骤：\n` +
  `1. 用 mcp__github__get_pull_request_status 查 Actions 状态\n` +
  `2. 如果还在 running/queued，sleep 30s 后重试（最多 ${Math.floor(timeoutMs / 30000)} 次）\n` +
  `3. 一旦 completed，用 mcp__github__get_pull_request_reviews 拉所有 review\n` +
  `   也用 mcp__github__get_pull_request_comments 拉行内评论\n` +
  `4. 把所有评论合并为结构化数组返回\n\n` +
  `返回 schema: { items: [{ id, user, body, path?, line?, state }] }\n` +
  `（注意：数组必须放在 items 字段下，不是顶层数组）\n` +
  `如果超时还没完成，也返回 { items: [] } 并说明原因。`,
  { phase: 'Poll', schema: REVIEW_SCHEMA, agentType: 'general-purpose' }
)

const reviews = result?.items ?? []
log(`PR #${prNumber} 拉到 ${reviews.length} 条 review`)
return reviews
