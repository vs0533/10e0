# Abstractions/ — 全局契约与标记接口

所有模块共享的根接口定义，不包含实现。

## 文件说明

| 文件 | 职责 |
|------|------|
| `EntityContracts.cs` | 实体标记接口：`IBaseEntity`（主键）、`ITimerEntity`（审计字段）、`ISoftDeleteEntity`（软删除）、`ITreeEntity`（树形） |
| `ICommand.cs` | 命令/查询根接口 + `Unit` 占位类型。不依赖 MediatR |
| `ICommandHandler.cs` | 命令处理器接口 `ICommandHandler<in TCommand, TResult>` |
| `ICommandDispatcher.cs` | 命令分发器接口，替代 `IMediator.Send` |
| `ICurrentUserContext.cs` | 当前用户上下文：同步读 Claims（零 I/O），异步加载详情。含 `UserType` 枚举和 `ICurrentUserInfo` |
| `IDataAccessPolicy.cs` | 数据访问策略：决定当前用户是否绕过所有行级过滤（超管检查） |
| `IErrs.cs` | 请求级错误收集袋：`Add(message, key, code)`、`IsValid`、`GetFirstError()` |
| `IPipelineBehavior.cs` | CQRS 管道行为接口，与 ASP.NET Core 中间件模式一致 |
| `JwtClaims.cs` | JWT Claim 类型常量：`sub`, `name`, `role`, `user_type` |

## 设计决策

- **主键统一为 `string`**：GUID 字面量，兼容多种 ID 策略（GUID/雪花/业务编号），收敛旧版类型不统一问题
- **`IQuery<T> : ICommand<T>`**：语义别名，当前与 Command 管道行为一致，预留读写分离扩展点
- **`UserType` 枚举**：`Person=0`（个人账号）、`Unit=1`（单位/机构账号），保留自旧 E0 的高价值领域区分
- **`ICurrentUserContext` 零 I/O 属性**：同步属性只读 `ClaimsPrincipal`，仅 `GetUserInfoAsync()` 走缓存/DB，杜绝旧版 `.Result` 阻塞隐患

## 被谁使用

几乎所有模块都引用此目录。这是框架的"地基"。
