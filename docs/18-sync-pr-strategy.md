# 同步 PR 合并策略（dev → main）

本文档说明 `10e0` 仓库里**同步型 PR**（base = main，head = dev，标题常含 `sync:` 或 `merge dev to main`）的合并方式选择，以及为什么必须用普通 merge commit，**不能用 squash merge**。

## 背景：仓库的分支角色

| 分支 | 角色 | 保护规则 |
|------|------|---------|
| `main` | **发版分支**（仅 dev → main merge 触发 `release.yml`） | linear history ✓、review ✓、no force-push |
| `dev` | **集成分支**（所有 feature PR 的 target） | 同上 |
| `feature/*` | **开发分支** | 无保护 |

只有当 `dev` 上累积了若干 feature PR、准备发版时，才会开一个**同步 PR** 把 dev 推到 main。这个 PR 触发 `release.yml` 完成 patch 版本号 + tag + GitHub Release + NuGet 发布。

## 核心规则

**同步 PR 必须用「Create a merge commit」合并，禁用「Squash and merge」。**

GitHub 合并按钮下拉里有三个选项（前提是仓库 Settings → General 启用了对应方式）：

| 合并方式 | 同步 PR 适用？ | 后果 |
|----------|----------------|------|
| **Create a merge commit** | ✅ **唯一正确** | main 上留下一个 merge commit，main 和 dev 之间的血缘关系保留，下次同步是快进 |
| **Squash and merge** | ❌ **禁止** | 把 dev 上所有 commit 压成 1 个放进 main，**血缘关系断裂** |
| **Rebase and merge** | ⚠️ 一般不用 | 会把 dev 的 commit 一对一重放到 main 上，但 linear history 规则 + dev 自己的分叉历史可能制造空 merge，且和本工作流语义不匹配 |

## 为什么 squash 会出问题（2026-06-05 真实案例）

### 症状

`PR #24`（sync dev → main，32 个 commit）显示 `mergeable_state: dirty`，本地点开冲突发现约 30 个文件报 add/add 冲突。但仔细看这些文件 —— **main 上的内容和 dev 上的内容其实一模一样**。

### 根因

Git 找共同祖先（merge base）的算法只看提交图，不看文件内容：

1. main 上 `ddb2a09` 是 PR #5 sync 时的 **squash commit** —— 它把当时 dev 的所有内容压成 1 个 commit 放进来
2. main **不记得**这个 commit 来自 dev
3. dev 继续往前走，dev 上有完整提交链
4. 当 PR #24（dev → main）合并时，Git 算出的共同祖先是 `6d99f10`（远在 squash 之前）
5. 在那个祖先时间点，`.github/workflows/*.yml`、新增的测试文件等**都不存在**
6. main 的 squash commit「加」了这些文件，dev 的历史 commit 也「加」了这些文件 → 报 **add/add 冲突**
7. Git 不会去做内容 diff 比对（那样会判定其实无冲突），它只看祖先图

### 时间线

```
82276eb  feat: initial 10E0 framework          (祖先)
   |
   +-- 6d99f10 chore: initial CI/CD (#1)       (共同祖先)
         |
         +-- origin/dev  [中间十几个 commit + 32 个新 commit 到 cc5c108]
         |
         +-- origin/main -- ddb2a09 sync (#5, SQUASHED) -- 6d99f10 ...
                              ↑
                              找不到和 dev 的血缘关系
```

## 正确的合并方式

### 步骤

1. 打开同步 PR（base = main，head = dev）
2. 等所有 CI 检查（`Build & Test` + `CodeQL`）变绿
3. 等 Claude Review bot（Qwen / MiniMax-M3）发出 `APPROVE`
4. 点击 **Merge pull request** 按钮的下拉箭头
5. 选择 **Create a merge commit**（默认选项）
6. 写合并信息（默认 `Merge branch 'dev' into main` 即可）
7. 确认 → `release.yml` 自动跑

合并后 main 的提交图大致是：

```
main:  ... → ddb2a09 (squash) → Merge commit (来自 dev 同步 PR) → ...
                                    ↑ 这次保留下来了
```

下一次同步 PR，Git 能从 merge commit 直接找到 dev 的祖先 → **3-way merge 干净**，不会再报假冲突。

## 怎么检查

合并前在本地复现：

```bash
git fetch origin main dev
git checkout -b check/sync origin/dev
git merge origin/main --no-commit --no-ff
```

- 如果只报少量真实冲突（dev 上有意识地改了同一段代码）→ 正常，解冲突后 push
- 如果报大量 add/add 冲突，且内容看起来一样 → 上一次同步被 squash 了，要么现在用 `git merge -X theirs` 强制走 dev 版本并接受历史脏，要么用 `git reset --hard origin/dev` 强制对齐（需要 admin）

## 自动化提醒

`.claude/agents/issue-prioritizer.md` 已经把这条规则接进 PR 处理流程：

- 识别条件：`baseRefName == "main" && headRefName == "dev"`
- 在 PR 报告的「处理建议」里加 **🔶 同步 PR 合并方式警告**
- 文字明确写："必须用 **Create a merge commit**，**禁止** Squash and merge，否则下次同步会报 ~30 个假冲突"

跑 `Workflow({ name: "triage-loop" })` 或 `/triage` 时，这条提醒会自动出现在 P0/P1 段。

## 常见问题

**Q: 为什么不直接用 fast-forward？**
A: GitHub 的 PR 合并按钮不能纯 fast-forward（会跳过"创建 merge commit"那一步）。同步 PR 通常会带新 commit，所以是 3-way merge 场景。

**Q: 之前的 PR #5 用了 squash 怎么办？要不要改？**
A: 历史 commit 改不了。但下次同步 PR 用 merge commit 合并后，main/dev 血缘会重新建立起来，从此恢复。

**Q: 如果不小心 squash 了同步 PR 怎么办？**
A: 别慌。修法：
```bash
git checkout main
git reset --hard origin/dev   # 强制 main 追上 dev
git push --force-with-lease    # admin 绕过保护规则
```
或下次同步 PR 用 `git merge -X theirs origin/dev`（冲突时强制采用 dev 版本）然后用 merge commit 合并。

**Q: GitHub 仓库设置能默认只允许某种合并方式吗？**
A: 能。Settings → General → Pull Requests → 取消勾选 "Allow squash merging" 即可。但项目里部分 PR 仍可能需要 squash（比如单个 feature 合并到 dev 时），所以**不建议全局禁用**，靠人/agent 识别同步 PR 后选对方式。

## 总结

> **同步 PR = Create a merge commit，禁 Squash。**
> **原因 = 维护 main/dev 血缘关系，避免下次同步报 30 个假冲突。**
> **机器识别 = base=main && head=dev，issue-prioritizer 自动加 🔶 警告。**
