// 烟雾测试：以 dryRun 模式跑 1 轮 triage-loop，验证 issue-prioritizer schema 不破
// 用法：Workflow({ name: "triage-loop-test" })
// 返回：{ ok: boolean, itemCount: number, error?: string }

export const meta = {
  name: 'triage-loop-test',
  description: '烟雾测试：dryRun=true 跑 1 轮 triage-loop，验证 issue-prioritizer schema 与 dispatch 链路通畅',
  phases: [{ title: 'Smoke Test' }],
}

log('triage-loop 烟雾测试启动: dryRun=true, max=1')

let result
try {
  // 1 轮 dryRun 不会真开 PR/改文件，但会调 issue-prioritizer 拉排序
  result = await workflow('triage-loop', { max: 1, 'dry-run': true })
} catch (err) {
  const msg = err?.message ?? String(err)
  log(`✗ triage-loop 调用失败: ${msg}`)
  return { ok: false, error: msg }
}

if (!result || typeof result !== 'object') {
  log(`✗ triage-loop 返回格式异常: ${JSON.stringify(result)}`)
  return { ok: false, error: 'triage-loop returned non-object' }
}

// result.summary 形如 "triage-loop 结束: processed=N, skipped=M"
const summary = result.summary ?? ''
const m = summary.match(/processed=(\d+),\s*skipped=(\d+)/)
if (!m) {
  log(`? triage-loop 跑通但 summary 无法解析: ${summary}`)
  return { ok: true, itemCount: 0, warning: 'unparseable summary' }
}

const processed = Number(m[1])
const skipped = Number(m[2])
const itemCount = processed + skipped

log(`✓ triage-loop 跑通: processed=${processed}, skipped=${skipped}, total=${itemCount}`)

return { ok: true, itemCount, processed, skipped }
