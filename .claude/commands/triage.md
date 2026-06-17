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
- **不要中途打断**：打断会让当前 item 处于半完成状态（停在某 feature 分支）。下次跑 BranchCheck 会强制回 dev，残留的未提交改动会被丢弃。
- **全自动合并到 dev**：CI 绿 + mergeable 时 Merge & Sync 阶段会自动 `squash` 合并 PR 到 dev 并同步本地 dev。**dev→main 发版仍需人工触发**。
- **branch protection 会拦自动合并**：dev 若要求 ≥1 人工 approve，merge 会被拒（422），PR 留在 open 等人工合并，循环继续不崩。

## 调试入口

单独跑子工作流处理一个 item：

```js
Workflow({ name: 'process-item', args: { item: { id: 42, type: 'issue', url, title, body, labels: [], priority: 1 } } })
```
