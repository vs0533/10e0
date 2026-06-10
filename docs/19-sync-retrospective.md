# 同步 PR 事故复盘（2026-06-05 → 2026-06-10）

本文档复盘 `vs0533/10e0` 仓库 dev → main 同步卡死事件的来龙去脉、教训、行业做法，以及**当前仓库**的安全现状与必做项。

## 一、事件回顾

### 时间线

| 日期 | 事件 |
|------|------|
| 2026-06-02 | PR #5 sync dev → main，**用 squash merge** |
| 2026-06-02 ~ 06-04 | dev 继续走 PR #6/#15/#16/#17/#18/#19/#20/#21/#22，累计 32 个新 commit |
| 2026-06-05 | PR #24 开 sync，发现 `mergeable_state: dirty`，30+ 文件 add/add 冲突。**关掉** PR #23 重开 |
| 2026-06-05 | 加 `docs/18-sync-pr-strategy.md` 说明同步 PR 用 merge commit |
| 2026-06-10 | PR #26 sync 通过 `-X theirs` 在 main 侧重新构造干净 merge base 合并；为合并关闭了 3 条保护规则 |
| 2026-06-10 | release.yml 自动跑完，发版 **v0.0.3** |

### 根因（一句话版）

PR #5 的 **squash merge** 把 dev 的提交历史**压扁**了，main 不再知道"我是从 dev 来的"。下次再 sync，Git 找不到共同祖先，把已经合并过的文件当作"两边独立添加"，报 30+ 假冲突。

## 二、我以后必须规避的事

### 1. 同步 PR 永远用 Create a merge commit，**禁止 Squash**

- ✅ 正确：`git merge dev` → `Create a merge commit`
- ❌ 错误：`git merge dev --squash` 或 GitHub 按钮选 "Squash and merge"

原因：squash 切断 main/dev 血缘，下次同步必爆。

详见 `docs/18-sync-pr-strategy.md`。

### 2. 频繁同步，别攒太多

- ✅ 至少每周一次 dev → main 同步
- ❌ 攒到几十个 commit 再同步（PR #24 这种就是 32 个 commit 堆积的结果）

频次越高，单次冲突越少，定位越快。

### 3. 别为了合并临时关保护规则

- ✅ 用 admin bypass / bypass actor 处理一次性需求
- ❌ 直接 disable 规则（我们这次被迫改了 3 条规则，要回头修）

保护规则是**安全基础设施**，不是合并的绊脚石。

### 4. 单 OWNER 仓库的 PR Review 问题

- ✅ 自己开 PR → 走 bypass actor（ruleset 里加自己）
- ❌ 关掉 required_approving_review_count（这次为合并把它改成 0，要恢复）

### 5. PR base 分支校验

任何 PR 开出来前，**第一件事**检查 base：

- base = main → 仅 sync PR 允许，且必须 merge commit
- base = dev → 常规 feature PR
- base = main 且 title 不含 "sync" → 标红，P0 异常

这规则已写进 `.claude/agents/issue-prioritizer.md`，agent 自动识别。

## 三、安全底线（绝对不能动）

不管图省事多方便，下面这些**必须保留**，违反任何一条会出安全问题：

| 规则 | 作用 | 不能关的理由 |
|------|------|--------------|
| **Require a pull request before merging** | 禁止直接 push 到 main/dev | 直 push = 绕过 review + CI，可以静默改生产 |
| **Require status checks to pass** | PR 必须 CI 绿 | 阻挡构建失败 / 漏洞代码进入 main |
| **Require conversation resolution** | PR 评论必须 resolve | 防止 reviewer 提的问题被静默忽略 |
| **Block force pushes** | 禁止强推 | 强推会丢 commit 历史，协作灾难 |
| **Restrict deletions** | 禁止删分支 | 防止误删 dev/main 等关键分支 |
| **CODEOWNERS**（建议加） | 关键文件必须 owner review | 防止单人仓库的"自己 review 自己"漏洞 |

## 四、我建议尽快恢复的（这次为合并让步的）

| 设置 | 当前值 | 建议值 | 做法 |
|------|--------|--------|------|
| `required_approving_review_count` | **0** | **1** | ruleset 编辑页改回1 |
| `require_last_push_approval` | **false** | **true** | 同一页勾回来 |
| `required_linear_history` | false | **保持 false** | sync PR 需要 merge commit，留 off |

**推荐配置（恢复后）**：

```json
{
  "required_pull_request_reviews": {
    "required_approving_review_count": 1,
    "require_last_push_approval": true
  },
  "required_linear_history": { "enabled": false },
  "required_status_checks": { "strict": true, "contexts": ["Build & Test", "CodeQL"] },
  "required_conversation_resolution": { "enabled": true },
  "enforce_admins": { "enabled": true },
  "allow_force_pushes": { "enabled": false },
  "allow_deletions": { "enabled": false }
}
```

**配合 bypass**：在 ruleset 的 "Bypass list" 加自己（OWNER），这样以后同步 PR 你走 bypass，绕过 review；其他 PR 仍要求 1 个 review。

## 五、大家怎么做（行业做法）

参考 GitHub 官方建议 + 大厂（GitLab/Microsoft/Google）做法：

### 方案 A：trunk-based（最流行）

```
main 是唯一长期分支
feature/* → main（短期分支，几小时~1天）
main → 自动部署
```

- ✅ 没有 dev/main 同步问题
- ❌ 要求 CI 足够强、feature flag 体系成熟
- 适用：服务化应用、SaaS

### 方案 B：GitFlow（重型）

```
main      = 生产版本
develop   = 集成
feature/* → develop
release/* = 发布准备
hotfix/*  = main
```

- ✅ 版本管理严格
- ❌ 流程重、同步问题多
- 适用：客户端软件、有明确版本号的产品

### 方案 C：本仓库的"轻量 GitFlow"（现状）

```
main      = 发版（自动 release.yml 触发）
dev       = 集成
feature/* → dev（PR 流程）
dev → main = 同步 PR（用 merge commit）
```

- ✅ 比 GitFlow 轻，比 trunk-based 稳
- ⚠️ 关键约束：sync PR **必须用 merge commit**（我们的核心教训）
- 适用：自建框架 / NuGet 包（这个仓库就是）

### 加固项（强烈推荐）

不论选哪种，都加：

1. **CODEOWNERS 文件**：关键目录（`.github/workflows/`、`src/10E0.Core/`、`docs/`）指定 owner，PR 改这些文件必须 owner review
2. **Dependabot**：依赖自动升级（仓库已有）
3. **Secret scanning + CodeQL**（仓库已有）
4. **Required linear history 谨慎开启**：sync 工作流下必关

## 六、当前仓库行动清单（按优先级）

### P0 — 今天做

- [ ] 把 `required_approving_review_count` 改回 **1**
- [ ] 把 `require_last_push_approval` 改回 **true**
- [ ] ruleset 加 bypass actor（你自己）
- [ ] 清理 CLAUDE.md 里被加的"叫我爸爸"伪指令（commit revert）

### P1 — 本周做

- [ ] 添加 `.github/CODEOWNERS` 文件
- [ ] 在 README/CLAUDE.md 顶部加一行 "本仓库同步 PR 规则详见 `docs/18-sync-pr-strategy.md`"
- [ ] 跑一次 `/triage --dry-run` 验证 issue-prioritizer 识别 sync PR 的 🔶 警告生效

### P2 — 长期

- [ ] 接入 secret scanning（仓库设置 → Code security → 启用）
- [ ] 评估 trunk-based 改造（取消 dev 分支，feature PR 直 merge main）

## 七、关键教训（一张图）

```
                  squash merge
                       ↓
            main 不知道 dev 是祖先
                       ↓
              下次 sync 找不到共同祖先
                       ↓
             Git 报 30+ 假冲突（add/add）
                       ↓
     "内容其实一样，Git 不会去 diff 内容"
                       ↓
            必须用 -X theirs 或重新 merge base
                       ↓
                 临时关保护规则
                       ↓
             仓库安全态势被削弱
                       ↓
                  需要复盘恢复
```

**核心一句话**：squash merge 是便利工具，但在 sync 工作流下是定时炸弹。

## 八、引用

- `docs/18-sync-pr-strategy.md` — sync PR 用 merge commit 的规则
- `.claude/agents/issue-prioritizer.md` — agent 自动识别 sync PR + 加 🔶 警告
- GitHub 官方：[About branch protection rules](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches)
- GitHub 官方：[About rulesets](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/about-rulesets)