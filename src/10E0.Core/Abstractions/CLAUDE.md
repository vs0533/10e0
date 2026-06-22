# Abstractions/ — 全局契约与标记接口

所有模块共享的根接口定义，不包含实现。

## 文件说明

| 文件 | 职责 |
|------|------|
| `EntityContracts.cs` | 实体标记接口：`IBaseEntity`（主键）、`ITimerEntity`（审计字段）、`ISoftDeleteEntity`（软删除）、`ITreeEntity`（树形）、`IMultiTenantEntity`（多租户，PR #11） |
| `ICommand.cs` | 命令/查询根接口 + `IQuery<T> : ICommand<T>` 语义别名 + `Unit` 占位类型。不依赖 MediatR |
| `ICommandHandler.cs` | 命令处理器接口 `ICommandHandler<in TCommand, TResult>` |
| `ICommandDispatcher.cs` | 命令分发器接口，替代 `IMediator.Send` |
| `ICurrentUserContext.cs` | 当前用户上下文：同步读 Claims（零 I/O），含 `RoleIds` / `RoleVersions` 字典（#7 instant permission revocation）；异步 `GetUserInfoAsync()`。含 `UserType` 枚举和 `ICurrentUserInfo` |
| `IDataAccessPolicy.cs` | 数据访问策略：决定当前用户是否绕过所有行级过滤（超管检查）。同文件含 `internal sealed class DefaultDataAccessPolicy` 默认实现 |
| `IErrs.cs` | 请求级错误收集袋：`Add(message, key, code)` / `IsValid` / `Entries` / `Keys` / `GetFirstError()` / `Clear()`；同文件含 `ErrorEntry` record |
| `IPipelineBehavior.cs` | CQRS 管道行为接口（含 `Order` 属性控制包裹顺序）与 `CommandHandlerDelegate<TResult>` 委托；与 ASP.NET Core 中间件模式一致 |
| `JwtClaims.cs` | JWT Claim 类型常量：`sub` / `name` / `role` / `user_type` / `role_versions` (#7) / `tenant_id` (#11)；同文件还含 `CacheKeys` 静态类（`user_info`，#37 兼容层） |
| `ITokenClaimNames.cs` | JWT/Token claim 名抽象注入点（PR #71 #37 Part 1），含默认实现 `JwtClaimsTokenClaimNames` |
| `ErrorCodes.cs` | 全局业务错误码常量中心（`public static class ErrorCodes`，PR #71 #37 Part 2） |
| `ICacheKeyNamespace.cs` | 缓存 key 命名空间抽象注入点（PR #71 #37 Part 3），含默认实现 `DefaultCacheKeyNamespace` |
| `ITenantContext.cs` | 当前租户上下文（PR #11），`TenantId` 属性供 EF Tenant Filter 读取；HTTP 实现见 `Auth/HttpTenantContext.cs` |

## 设计决策

- **主键统一为 `string`**：GUID 字面量，兼容多种 ID 策略（GUID/雪花/业务编号），收敛旧版类型不统一问题
- **`IQuery<T> : ICommand<T>`**：语义别名，当前与 Command 管道行为一致，预留读写分离扩展点
- **`UserType` 枚举**：`Person=0`（个人账号）、`Unit=1`（单位/机构账号），保留自旧 E0 的高价值领域区分
- **`ICurrentUserContext` 零 I/O 属性**：同步属性只读 `ClaimsPrincipal`，仅 `GetUserInfoAsync()` 走缓存/DB，杜绝旧版 `.Result` 阻塞隐患
- **常量可注入抽象**（PR #71 #37）：把 JWT claim 名（`ITokenClaimNames`）/ 错误码（`ErrorCodes` 静态中心）/ 缓存 key（`ICacheKeyNamespace`）从硬编码抽成可注入的契约，让业务项目可重命名/扩展而无需 fork Core。`ErrorCodes` 是 static class 不是 interface（与其他两个的 interface+默认实现模式不同 —— 错误码是纯字符串常量，注入不带来扩展价值）
- **`IMultiTenantEntity` 标记驱动**（PR #11）：业务实体加此标记后 `BaseDataContext` 自动注册 Named Query Filter `Tenant`，表达式 `BypassFilters || (e.TenantId == currentTenantId)`，跨租户查询零业务代码
- **`ITenantContext` 抽象**（PR #11）：HTTP / Ambient 实现解耦，EF Filter 走表达式树绑定，DI 自动解析当前请求租户
- **`IErrs` 是请求级 bag**（非全局）：每个 HTTP 请求一个实例，通过 `HttpContext.Items` 或 scoped DI 传递，handler 完成后清空

## 被谁使用

几乎所有模块都引用此目录。这是框架的"地基"。