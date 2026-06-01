# Cqrs/Behaviors/ — 内置管道行为

CQRS Pipeline Behavior 实现，在命令执行前后插入横切逻辑。

## 文件说明

| 文件 | 职责 |
|------|------|
| `LoggingBehavior.cs` | 在命令执行前后记录日志（命令类型、耗时、结果） |
| `TransactionBehavior.cs` | 对标记 `ITransactional` 的命令包裹数据库事务。**修复旧 BUG-001**：用 Savepoint 替代嵌套事务，避免内层回滚破坏外层 |

## 注册顺序

在 `CqrsServiceCollectionExtensions` 中注册顺序决定执行顺序：

```csharp
services.AddTransient<IPipelineBehavior<,>, LoggingBehavior<,>>();     // 最外层
services.AddTransient<IPipelineBehavior<,>, TransactionBehavior<,>>(); // 内层
services.AddTransient<IPipelineBehavior<,>, PermissionBehavior<,>>(); // 最内层
```

## 注意事项

- `TransactionBehavior` 需要 DbContext，只在有数据库操作的命令上生效
- 事务使用 `BeginTransactionAsync` + `CommitAsync`，异常时 `RollbackAsync`
