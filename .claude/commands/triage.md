---
description: 循环分诊仓库 issues/PRs — 调用 triage-loop 工作流
---

# /triage — 自动循环处理 issue/PR 队列

调用 `.claude/workflows/triage-loop.js` 工作流，详见 `triage-loop` skill 与 SKILL.md。

## 用法

```
/triage                       # 默认 max=3
/triage --max 20              # 跑到 20 轮
/triage --issues-only         # 只处理 issue
/triage --prs-only            # 只处理 PR
/triage --labels bug,p1       # 只处理带这些 label 的
/triage --dry-run             # 仅显示队列，不实际派单
```

多个 flag 可组合，例如：
```
/triage --max 10 --issues-only --labels "bug,p0,critical"
```

## 行为

将 args 解析为对象，然后调用：

```js
Workflow({
  name: 'triage-loop',
  args: {
    max: <从 --max 解析, 默认 3>,
    'issues-only': <bool>,
    'prs-only': <bool>,
    labels: <csv 字符串或 null>,
    'dry-run': <bool>,
  },
})
```

## 注意事项

- **首次跑必先 `--dry-run`**：看队列里到底有什么再决定 max。
- **默认 max=3**：清完积压请 `--max 50`，跑 1-2 小时看效果。
- **跑起来后会持续输出 `[triage-loop]` 日志**到终端。
- **遇到 followup issue**：会标 `followup-from:#<pr>` 让 issue-prioritizer 下一轮降优先级。
- **不要中途打断**：打断会让当前 item 处于半完成状态。
- **跑完一批看 PR**：工作流会自动开 PR 到 dev，等用户确认后再合入 main。

## 调试入口

如果 triage-loop 报错，先跑 `/triage-loop-test`（或 `Workflow({ name: "triage-loop-test" })`）做一次烟雾测试。

也可以单独跑子工作流处理一个 item：

```js
Workflow({ name: 'process-item', args: { item: {...} } })
Workflow({ name: 'wait-for-pr-review', args: { prNumber: 7, prUrl: '...', timeoutMs: 600000 } })
```
