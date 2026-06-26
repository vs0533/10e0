# Workflow/ — 轻量审批流引擎

企业应用核心审批场景的基础设施。定位"轻量"——不追求 BPMN 2.0，覆盖 80% 企业审批场景（请假/报销/采购/合同/工单）。

详见 `docs/21-workflow.md`。

## 子目录

| 目录 | 职责 | 核心类型 |
|------|------|----------|
| `StateMachine/` | 状态机引擎（#157，独立可用） | `StateMachine<TState,TAction>`, `StateMachineBuilder`, `StateMachineDefinition`, `IStateMachineRegistry`, `GuardFailedException` |
| `Definitions/` | 流程定义（#158，节点 + 路由 + 条件） | `ProcessBuilder`, `IProcessNode`, `ApprovalNode`, `BranchNode`, `IAssigneeResolver`, `ConditionEvaluator`, `IProcessDefinitionStore`, `TenE0ProcessDefinition` |
| `Runtime/` | 流程运行时（#159，实例 + 任务 + 操作） | `IProcessRuntimeService`, `WorkflowEngine`, `ITaskService`, `IProcessActionHandler`, `TimeoutProcessor`, `TenE0ProcessInstance`/`Task`/`History` |
| `DependencyInjection/` | DI 扩展 | `WorkflowExtensions.AddTenE0WorkflowStateMachine/Definitions/Runtime` |

## 依赖顺序

`StateMachine`（独立）← `Definitions`（复用转换原语）← `Runtime`（消费定义 + 状态机事件）。

三模块按需组合注册，与项目其他 `AddTenE0Xxx<TContext>()` 一致。

## 关键设计点

- **状态机不持久化**：实体自带 State 字段，引擎只负责"判断 + 触发"
- **节点图 JSON 存储**：`NodesJson` 多态序列化（discriminator `$nodeType`），避免关系表 JOIN 复杂度
- **条件求值独立**：`ConditionEvaluator` 复用 `DynamicFilters.ConditionRuleGroup` 模型但独立求值业务数据字典
- **审批人解析解耦**：`IAssigneeDirectory` 抽象把"角色/组织 → 用户"查询从 Core 推到宿主层
- **乐观并发**：Instance/Task/Definition 都有 RowVersion（对齐 #100 序列号模式）
- **路由节点穿越**：Start/Branch 无人审批，engine 自动穿越到首个 Approval/Parallel 节点

## 框架表

`TenE0ProcessDefinition` / `TenE0ProcessInstance` / `TenE0ProcessTask` / `TenE0ProcessHistory` —— 由 `TenE0SystemDbContext` 自动注册，业务 DbContext 继承即获得。
