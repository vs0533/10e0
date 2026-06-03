# 贡献指南

欢迎参与 10E0（TenE0）的开发！本文档面向所有希望为项目贡献代码、文档、测试或反馈的开发者。请在提交 Pull Request 之前完整阅读以下内容。

## 行为准则

参与本项目的所有贡献者应秉持专业、尊重、包容的态度：欢迎不同背景的新人，对建设性批评保持开放，专注于对项目最有利的技术决策，杜绝任何形式的骚扰、歧视或人身攻击。所有沟通请使用中文或英文，并假设其他贡献者怀有善意。

## 报告问题 / 提交 Issue

提交 Issue 前，请先搜索现有 Issue 列表，确认问题未被重复报告。Issue 分为以下几类，请在创建时打上对应标签：

- **Bug Report** — 描述复现步骤、期望行为、实际行为、运行环境（.NET SDK 版本、操作系统、数据库类型与版本）。如果可能，请附带最小可复现代码或单元测试。
- **Feature Request** — 描述希望解决的问题、提议的 API 形态、与现有设计的兼容性影响。请先在 Issue 中讨论共识，再开始实现。
- **Documentation** — 指出文档中不准确、缺失或难以理解的部分，建议补充示例。
- **Question / Discussion** — 使用 Discussions 标签进行开放式讨论。

请提供尽可能具体的上下文，包括 commit SHA、分支名、相关文件路径。

## 开发流程

1. **Fork 仓库** — 在 GitHub 上 Fork `vs0533/10e0` 到你的个人账户。
2. **同步默认开发分支**：
   ```bash
   git checkout dev
   git pull origin dev
   ```
3. **创建特性分支**（分支命名建议 `feature/xxx`、`fix/xxx`、`docs/xxx`）：
   ```bash
   git checkout -b feature/your-feature-name
   ```
4. **实施 + 编写测试** — 遵循 TDD 流程：先写失败测试，再写最小实现使其通过，最后重构。
5. **本地验证**：
   ```bash
   dotnet build 10e0.slnx   # 必须零警告
   dotnet test  10e0.slnx   # 所有测试必须通过
   ```
6. **推送并创建 Pull Request** — PR 目标分支为 `dev`，**不是** `main`。`main` 分支仅接收 dev→main 合并并自动触发发版。

## 编码规范

- **遵循 `.editorconfig`** — 项目根目录的 `.editorconfig` 在构建时强制执行代码风格（`EnforceCodeStyleInBuild = true`）。请确保 IDE 启用了 EditorConfig 支持。
- **不可变性优先** — 始终创建新对象而非就地修改现有对象。使用 `record` 类型、`with` 表达式、返回新副本的 `Update` 方法，避免 setter 突变。
- **文件 < 800 行，函数 < 50 行** — 保持高内聚、低耦合。当模块过大时按职责拆分为多个文件。
- **深度嵌套 ≤ 4 层** — 通过早返回（early return）、策略模式或提取方法降低嵌套深度。
- **不提交生成文件** — `coverage.json`、`*.xml`、`.DS_Store` 等必须加入 `.gitignore`。`.omo/` 目录是内部工具文件，必须被排除。
- **不用 `git add -A`** — 必须显式指定文件，或使用 `git add -p` 逐块确认暂存内容。提交前执行 `git diff --cached --stat` 检查。
- **启用 NRT** — 解决方案全局启用 `<Nullable>enable</Nullable>`，所有引用类型必须显式标注可空性。
- **TreatWarningsAsErrors** — 任何警告都会导致构建失败，请保持零警告。

## 测试要求

- **测试框架**：xUnit + FluentAssertions + Moq。
- **测试组织**：
  - `tests/10E0.Core.Tests/` — Core 单元测试（xUnit + EF Core InMemory + coverlet）
  - `tests/10E0.Api.Tests/`  — Api 集成测试（xUnit + WebApplicationFactory）
- **覆盖率门槛 ≥ 80%** — CI 强制 gate，PR 必须达到此门槛才能合并。
- **TDD 工作流**：
  1. 先写一个失败测试（RED）
  2. 编写最小实现使其通过（GREEN）
  3. 重构以改善设计（IMPROVE）
  4. 验证覆盖率仍 ≥ 80%
- **测试独立性** — 每个测试应能独立运行，不依赖执行顺序或共享状态。
- **覆盖范围** — 单元测试、集成测试与端到端测试三种类型缺一不可。修复 Bug 时必须先添加能复现 Bug 的回归测试，再修复。

## 提交信息规范

采用约定式提交（Conventional Commits）格式：

```
<type>: <description>

<optional body>
```

**type 取值**：

| 类型 | 用途 |
|------|------|
| `feat` | 新功能 |
| `fix`  | Bug 修复 |
| `refactor` | 重构（既不修复 Bug 也不增加功能） |
| `docs`  | 仅文档变更 |
| `test` | 仅测试变更 |
| `chore` | 构建、CI、依赖更新等杂项 |
| `perf` | 性能优化 |
| `ci`   | CI/CD 流程变更 |

**示例**：

```text
feat: 添加 Permission Key 分布式缓存失效通知

- 引入 INotificationHandler<PermissionCacheInvalidated>
- 在 PermissionService.ReloadAsync 中发布领域事件
- 新增 PermissionCacheInvalidatedHandlerTests 覆盖三种场景
```

```text
fix: TransactionBehavior 使用 Savepoint 修复嵌套事务 Bug

CommandManager 在嵌套事务时会抛出 InvalidOperationException，
原因是 SaveChanges 后子事务无法回滚到父事务的已变更状态。
改用 EF Core 的 Savepoint API 后，嵌套回滚可正常生效。
```

- 描述使用中文或英文皆可，祈使语气，首字母不大写，末尾不加句号。
- `feat` 和 `fix` 类型若影响用户可见行为，请在 `CHANGELOG.md` 的 `## [Unreleased]` 章节追加条目。
- 通过 `~/.claude/settings.json` 全局禁用了归因（Co-Authored-By）信息，请勿手动添加。

## Pull Request Checklist

在请求评审之前，请确认以下所有项：

- [ ] `dotnet build 10e0.slnx` 通过（**零警告**，TreatWarningsAsErrors）
- [ ] `dotnet test 10e0.slnx` 全部通过
- [ ] 测试覆盖率 **≥ 80%**（新代码行覆盖率建议 ≥ 90%）
- [ ] PR 目标分支为 **`dev`**，不是 `main`
- [ ] 已执行 `git diff --cached --stat` 确认暂存内容无误
- [ ] 没有提交生成文件（`coverage.json`、`*.xml`、`.DS_Store`、`.omo/` 等）
- [ ] 遵循 `.editorconfig` 与项目编码规范
- [ ] **相关文档已同步更新**（任何代码 / 行为 / CI 变更都要逐项检查）：
  - `docs/*.md` — 用户面向的功能 / API 文档
  - 受影响模块的 `src/10E0.Core/**/CLAUDE.md` — 模块设计决策与文件清单
  - `.github/CLAUDE.md` — 如改了 workflow / dependabot / CodeQL
  - `tests/CLAUDE.md` — 如新增测试文件或覆盖率显著变化
- [ ] `CHANGELOG.md` 的 `## [Unreleased]` 章节已更新（如适用），条目链接回本 PR 编号
- [ ] 新增或修改的公共 API 提供 XML 文档注释
- [ ] 未引入未经讨论的破坏性变更（破坏性变更需先开 RFC Issue 达成共识）

---

感谢你的贡献！如有任何疑问，可在 Issue 或 Discussions 中提出。
