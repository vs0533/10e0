# Cqrs/ — 自建 CQRS 分发器

替代 MediatR 的轻量级命令分发实现。

## 文件说明

| 文件 | 职责 |
|------|------|
| `CommandDispatcher.cs` | `ICommandDispatcher` 默认实现。Wrapper 模式 + 静态缓存，零热路径反射。Pipeline Behaviors 逆序包裹，先注册的行为最先进入 |

## 工作原理

```
命令进入 → [Logging] → [Transaction] → [Permission] → Handler
                 ↑            ↑              ↑
           外层 behavior    中层          内层（最后进入、最先退出）
```

1. 按命令具体类型查找/创建 `CommandHandlerWrapper<TCommand, TResult>`（静态 `ConcurrentDictionary` 缓存）
2. 从 DI 取所有 `IPipelineBehavior<TCommand, TResult>`
3. 逆序包裹行为链，起点是真正的 Handler
4. 执行管道

## 设计决策

- **对比旧 `CommandManager.Dispatch()`**：旧版用嵌套事务循环 + MediatR，有 BUG-001（重复执行问题）。新版每个命令执行恰好一次
- **Wrapper 模式**：参考 MediatR 设计但完全独立实现，避免许可证依赖
- **Scoped 生命周期**：接收请求作用域的 `IServiceProvider`，可解析 Scoped Handler

## 子目录

| 目录 | 职责 |
|------|------|
| `Behaviors/` | 内置管道行为（日志、事务） |
