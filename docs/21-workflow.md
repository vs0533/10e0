# 21 — 工作流引擎（轻量审批流）

覆盖企业应用最核心的审批场景（请假/报销/采购/合同/工单）。定位**轻量审批流**——不追求 BPMN 2.0 标准，覆盖 80% 企业审批场景：顺序/并行审批、角色/上级/指定人审批、条件分支、会签/或签/委派/加签/回退。

## 模块结构

```
src/10E0.Core/Workflow/
├── StateMachine/      # 状态机引擎（独立可用，非审批场景也能用）
├── Definitions/       # 流程定义（节点 + 路由 + 条件 + 审批人解析）
├── Runtime/           # 流程运行时（实例 + 任务 + 操作 + 超时）
└── DependencyInjection/WorkflowExtensions.cs
```

三个子模块按依赖顺序组合：
1. **StateMachine**（#157）—— 独立，最小可用，可先发版验证
2. **Definitions**（#158）—— 依赖 #157 的转换原语
3. **Runtime**（#159）—— 依赖 #157 + #158

## DI 注册

```csharp
// Program.cs
builder.Services.AddTenE0WorkflowStateMachine(typeof(Program).Assembly);  // 扫描 IStateMachineDefinition
builder.Services.AddTenE0WorkflowDefinitions<DemoDbContext>();            // 流程定义存储 + 审批人解析
builder.Services.AddTenE0WorkflowRuntime<DemoDbContext>();                // 运行时 + 超时后台处理器

// AssigneeDirectory：把"角色/组织 → 用户"查询从 Core 解耦到宿主层
builder.Services.AddScoped<IAssigneeDirectory, AssigneeDirectory<DemoDbContext>>();
```

所有 `TenE0` 前缀的工作流表（`TenE0ProcessDefinition` / `TenE0ProcessInstance` / `TenE0ProcessTask` / `TenE0ProcessHistory`）由 `TenE0SystemDbContext` 自动注册——业务 DbContext 继承它即获得全部表。

---

## 第一部分：状态机（#157）

状态机是审批流的底层原语，但也可独立用于非审批场景（订单状态、工单状态、文档生命周期）。

### 核心概念

| 概念 | 说明 |
|------|------|
| `StateMachineDefinition<TState,TAction>` | 不可变定义（Freeze 后只读），含初始状态/动作转换/白名单/Guard |
| `StateMachineBuilder` | Fluent API 构造器 |
| `StateMachine<TState,TAction>` | 引擎：`FireAsync` 触发转换，返回 `(newState, transition)` |
| `StateTransition` | 转换记录（From/To/Action/Actor/Reason/Timestamp） |
| Guard | 守卫条件，多个全部通过才允许转换 |
| `IStateMachineRegistry` | 启动期扫描注册，运行时 O(1) 查找 |

### Fluent API

```csharp
var sm = StateMachine<OrderState, OrderAction>.Create(OrderState.Draft)
    .On(OrderAction.Submit).Transit(OrderState.Draft).To(OrderState.Submitted)
        .Guard<Order>(o => o.Items.Count > 0, "ORDER_NO_ITEMS")        // 同步 Guard
    .On(OrderAction.Approve).Transit(OrderState.Submitted).To(OrderState.Approved)
    .On(OrderAction.Cancel).FromAny().To(OrderState.Cancelled)         // 从任意状态可取消
        .Guard<Order>(o => o.State != OrderState.Completed, "ORDER_ALREADY_COMPLETED")
    .Build();

var (newState, transition) = await sm.FireAsync(entity.State, OrderAction.Submit, entity, "u001");
entity.State = newState;  // 业务方回写
```

### 两种转换语义

| 语义 | 语法 | 场景 |
|------|------|------|
| 动作驱动 | `.On(A).Transit(From).To(To)` | 最常用，"在状态 X 触发动作 Y → 到达 Z" |
| FromAny 直转 | `.On(A).FromAny().To(To)` | 典型如 Cancelled/Closed，任意状态都可触发 |

### Guard（守卫条件）

```csharp
.On(OrderAction.Submit).Transit(OrderState.Draft).To(OrderState.Submitted)
    .Guard<Order>(o => o.Items.Count > 0, "ORDER_NO_ITEMS")
    .Guard<Order>(o => o.Amount > 0, "ORDER_INVALID_AMOUNT")
    .GuardAsync<Order>((o, ct) => CheckInventoryAsync(o, ct), "INVENTORY_INSUFFICIENT")
```

- 多个 Guard 全部通过才允许转换
- 任一失败抛 `GuardFailedException`（携带所有失败 Reasons 错误码，便于前端 i18n）
- Reason 用静态错误码（对齐 `ErrorCodes.cs` 约定）

### 转换事件

通过注入的 `IDomainEventDispatcher`（可选）派发三组事件，顺序模拟"离开旧 → 发生转换 → 进入新"：

| 事件 | 时机 |
|------|------|
| `StateExitedEvent<T>` | 离开旧状态 |
| `StateTransitionEvent<T,TState,TAction>` | 转换发生（携带完整 StateTransition） |
| `StateEnteredEvent<T>` | 进入新状态 |

业务方可订阅 `StateTransitionEvent<T>` 做副作用：通知（#155）、审计（#152）、流程驱动（#159）。

### 注册定义

```csharp
public sealed class OrderStateMachineDefinition
    : StateMachineDefinitionBase<OrderState, OrderAction>
{
    public override StateMachineDefinition<OrderState, OrderAction> Define()
        => StateMachine.Create<OrderState, OrderAction>(OrderState.Draft)
            .On(OrderAction.Submit).Transit(OrderState.Draft).To(OrderState.Submitted)
            .And().Build();
}

// 启动期 AddTenE0WorkflowStateMachine 扫描注册，运行时：
var sm = registry.Get<OrderState, OrderAction>();
```

---

## 第二部分：流程定义（#158）

把审批流程抽象为节点 + 连线 + 条件的有向图。只解决"流程长什么样"，运行时由 #159 承载。

### 节点类型

| 类型 | 说明 |
|------|------|
| `StartNode` | 开始节点（流程唯一入口），必填 `NextNodeCode` |
| `EndNode` | 结束节点（至少一个） |
| `ApprovalNode` | 审批节点（人审）：Mode/AssigneePolicy/Permission/AllowDelegate/AllowAddSigner/AllowRollback/Timeout |
| `BranchNode` | 条件分支：按 `BranchRoute.Condition`（复用 `ConditionRuleGroup`）路由 |
| `ParallelNode` | 并行节点：多分支并发，全部完成才推进 |

### 审批模式

| 模式 | 判定 |
|------|------|
| `Single` | 任一审批人 Approve 即通过 |
| `Countersign` | 全部审批人 Approve 才通过（会签） |
| `OrSign` | N 人中任一 Approve 即通过（或签） |

### 审批人解析

`AssigneePolicy` 声明审批人来源，`IAssigneeResolver` 按 `AssigneePolicyKind` 匹配执行：

| Kind | 来源 |
|------|------|
| `Role` | 某角色的所有用户（走 `IAssigneeDirectory.GetUsersByRoleAsync`） |
| `Manager` | 直接上级（走组织树 + `IAssigneeDirectory`） |
| `NLevelManager` | N 级上级 |
| `User` | 指定用户列表 |
| `Expression` | 表达式（如 `initiator` / `initiator.org.members`） |

`IAssigneeDirectory` 是把"角色/组织 → 用户"查询从 Core 解耦到宿主层的抽象——Core 的 Resolver 不依赖具体 DbContext，由 Api 层实现（查 `TenE0UserRole` + 组织树）。

### Fluent API

```csharp
var def = ProcessBuilder.Create("expense-claim", "费用报销")
    .Category("finance")
    .Start("start", "manager")
    .Approval("manager", "直属主管审批")
        .Assignee(AssigneePolicy.Manager())
        .Mode(ApprovalMode.Single)
        .Permission("expense.approve")
        .AllowRollback("manager")
        .Next("amount-check")
    .Branch("amount-check")
        .When("Amount", "gt", "10000", "director")   // 金额>10000 走总监
        .Default("end")                                // 否则直过
    .Approval("director", "财务总监会签")
        .Assignee(AssigneePolicy.Role("finance-director"))
        .Mode(ApprovalMode.Countersign)
        .Next("end")
    .End("end")
    .Build();  // 产出 TenE0ProcessDefinition（含序列化 NodesJson + Build 期校验）
```

### 条件求值

分支条件复用 `DynamicFilters.ConditionRuleGroup` 模型与操作符语义（eq/ne/gt/gte/lt/lte/contains/startsWith/endsWith/in/notIn），但求值对象是**业务数据字典**（`Dictionary<string,object?>`）而非 EF 实体。

`ConditionEvaluator.Evaluate(group, data, initiator, initiatorOrgId)` 对字典求值，支持 `{loginUser}`/`{loginOrg}` 占位符。

> 与 `FilterExpressionBuilder` 的关系：语义复用（同一套操作符约定），实现独立（FilterExpressionBuilder 输出 EF LambdaExpression 面向实体 + DbContext，无法用于运行时业务数据字典）。

### 流程图校验

`ProcessDefinitionValidator` 在 Build 期校验：

- 有且仅有一个 Start 节点
- 至少一个 End 节点
- 所有 NextNodeCode / BranchRoute.TargetNodeCode 指向的节点存在
- 审批节点必须有 AssigneePolicy
- `AllowRollback=true` 必须有 RollbackTargetCode 且目标存在
- Branch 节点必须有 DefaultNodeCode
- 无死节点、无环路（除显式回退外）

校验失败抛 `ProcessDefinitionInvalidException`（携带所有问题列表）。

### 版本管理

`TenE0ProcessDefinition` 同 `Code` 下多版本，`IsLatest=true` 标识当前生效版本。`IProcessDefinitionStore.PublishAsync` 发布新版本时自动把旧版本 `IsLatest` 置 false、新版本号 = 旧 max + 1。已启动实例锁定创建时的 Version，模板改版不影响存量。

---

## 第三部分：流程运行时（#159）

把模板实例化为具体的一次审批：发起 → 任务流转 → 审批/委派/会签/回退 → 完成。

### 实体

| 实体 | 说明 |
|------|------|
| `TenE0ProcessInstance` | 流程实例（绑定业务实体 + 锁定 Definition 版本） |
| `TenE0ProcessTask` | 审批任务（每个审批人一条，便于委派/加签/独立超时） |
| `TenE0ProcessHistory` | 审批历史（只追加，不修改不删除，保证审计完整性） |

Instance 和 Task 都有 `RowVersion` 乐观并发控制（对齐 #100 序列号并发模式）。

### 核心 API

```csharp
public interface IProcessRuntimeService
{
    Task<ProcessInstanceDto> StartAsync(StartProcessRequest req, CancellationToken ct);
    Task<ProcessActionResult> ExecuteActionAsync(ExecuteActionRequest req, CancellationToken ct);
    Task CancelAsync(string instanceId, string actor, string? reason, CancellationToken ct);
}
```

`ExecuteActionAsync` 按 `ActionKind` 分发到对应 handler。

### 操作动作

| Action | 行为 |
|--------|------|
| **Approve** | 标记 Task 完成；按 ApprovalMode 判定节点是否通过；通过则推进 |
| **Reject** | 标记 Task 拒绝；同节点其他 Task 作废；实例置 Rejected |
| **Delegate** | 当前 Task 标记 Delegated；为被委派人新建同节点 Task（需 `AllowDelegate`） |
| **AddSigner** | 当前节点追加 Task（需 `AllowAddSigner`） |
| **Rollback** | 当前节点终止；回退到 `RollbackTargetCode`；重新生成该节点 Task（需 `AllowRollback`） |

**权限校验**：执行操作前校验 `Actor == Task.Assignee`，且节点配置允许该操作。

### 引擎推进逻辑

`WorkflowEngine.TryAdvanceAsync`：
1. 解析当前节点所有 Task
2. 按 ApprovalMode 判定：Countersign 全部 Approve 才通过；Single/OrSign 任一 Approve 即通过；任一 Rejected 即终止
3. 通过后**穿越路由节点**（Start/Branch 无人审批，自动按 NextNodeCode/条件路由推进），停在首个 Approval/Parallel 节点或 End
4. 为新节点创建 Task + 触发 `ProcessNodeEnteredEvent`
5. 遇到 End → 实例完成（Approved），触发 `ProcessCompletedEvent`

### 领域事件

| 事件 | 时机 | 用途 |
|------|------|------|
| `ProcessStartedEvent` | 流程启动 | 审计 |
| `ProcessNodeEnteredEvent` | 进入新节点（产生新任务） | 推送审批通知（#155） |
| `ProcessCompletedEvent` | 流程完成 | 通知发起人 |
| `ProcessCancelledEvent` | 流程撤销 | 通知 |

本框架只定义事件契约。推送（#155）和审计（#152）的订阅者属各自 issue，在此消费本组事件。

### 超时处理

`TimeoutProcessor<TContext>` 是 `BackgroundService`（参考 `OutboxRelayService` 模式），定期扫描 `Status=Pending AND Deadline < now` 的任务，按节点 `TimeoutAction` 执行：

| TimeoutAction | 行为 |
|---------------|------|
| `NotifyOnly` | 标记 Timeout，触发 `ProcessNodeEnteredEvent` 供推送订阅提醒 |
| `AutoApprove` | 自动通过 |
| `AutoReject` | 自动驳回 |

扫描间隔可配（`WorkflowRuntimeOptions.TimeoutScanInterval`，默认 1 分钟）。

### 待办查询

```csharp
public interface ITaskService
{
    Task<WorkflowPagedResult<TaskDto>> GetMyPendingTasksAsync(string userCode, ...);   // 我的待办
    Task<WorkflowPagedResult<ProcessInstanceDto>> GetMyInitiatedAsync(string userCode, ...);  // 我发起的
    Task<IReadOnlyList<HistoryDto>> GetInstanceHistoryAsync(string instanceId, ...);  // 审批历史
    Task<ProcessInstanceDto?> GetInstanceAsync(string instanceId, ...);  // 实例详情
}
```

---

## HTTP 端点

### 业务端点（`/workflow/*`，需认证）

| 方法 | 路径 | 说明 |
|------|------|------|
| POST | `/workflow/start` | 发起流程 |
| POST | `/workflow/{id}/actions` | 执行审批操作 |
| POST | `/workflow/{id}/cancel` | 撤销（仅发起人） |
| GET | `/workflow/tasks/pending` | 我的待办 |
| GET | `/workflow/initiated` | 我发起的 |
| GET | `/workflow/{id}` | 实例详情 |
| GET | `/workflow/{id}/history` | 审批历史 |

### 管理端点（`/admin/workflow/*`，需 admin）

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/admin/workflow/definitions` | 列出所有最新版本 |
| GET | `/admin/workflow/definitions/{code}/versions` | 版本列表 |
| GET | `/admin/workflow/definitions/{code}/latest` | 最新版本详情 |
| POST | `/admin/workflow/definitions` | 发布新版本（完整 JSON） |
| POST | `/admin/workflow/definitions/built-in/{builtin}` | 发布预置流程（如 `expense-claim`） |
| DELETE | `/admin/workflow/definitions/{id}` | 禁用（不物理删除） |

---

## 设计决策

1. **状态机独立性**：`StateMachine` 不依赖 `Definitions/Runtime`，可单独用于非审批场景
2. **节点图 JSON 存储**：而非关系表（流程结构灵活、序列化性能足够、避免 7-8 张关系表的 JOIN 复杂度）
3. **条件复用 + 实现独立**：分支条件复用 `ConditionRuleGroup` 模型，但用独立的 `ConditionEvaluator` 求值业务数据字典（不扭曲 EF 表达式路径）
4. **审批人解析解耦**：`IAssigneeResolver` 只依赖 `IAssigneeDirectory` 抽象，Core 不依赖具体 DbContext
5. **版本不可变**：已发布 Definition 不可修改，只能发新版本（保证存量实例可重现性）
6. **任务粒度**：每个审批人一条 Task（便于委派/加签/独立超时）
7. **多租户**：Definition/Instance 自动带 TenantId（实现 `IMultiTenantEntity`）
8. **乐观并发**：Instance/Task 用 RowVersion（对齐 #100 序列号并发模式）

## 不在本期范围

- BPMN 可视化建模（后续 epic）
- 定时器节点、子流程、复杂网关（明确排除）
- WebSocket 推送的审批通知订阅者（属 #155）
- 审计落库的审批订阅者（属 #152）
