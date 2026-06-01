# Permissions/Behaviors/ — CQRS 管道权限行为

## 文件说明

| 文件 | 职责 |
|------|------|
| `PermissionBehavior.cs` | CQRS Pipeline Behavior：在命令执行前检查 `[RequirePermission]` 属性。无权限则抛 `PermissionDeniedException` |

## 工作流程

```
命令进入 PermissionBehavior
    → 检查 Command 类型是否有 [RequirePermission("key")] 属性
    → 有：调用 IPermissionEvaluator.HasAsync(key)
        → true：放行到下一个 Behavior / Handler
        → false：抛出 PermissionDeniedException
    → 无属性：直接放行
```

## 对比旧版

- 旧版 `BaseEntityServer.Authorization()` 在 Handler 内部手动检查
- 新版声明式（属性标记）+ 管道自动拦截，Handler 不感知权限逻辑

## 注册顺序

`PermissionBehavior` 是最内层 Behavior（最后注册），在 `LoggingBehavior` 和 `TransactionBehavior` 之后执行。确保权限检查在事务内，且在日志之后。
