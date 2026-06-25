# 23 — 实时推送（Realtime / SignalR）

声明式 SignalR 推送：**领域事件实现 `INotifyClient` 即自动推送给前端，业务方零样板**。组体系基于 JWT claims 零 I/O 派生（user / role / tenant / org）。多副本预留 backplane 抽象。

本 issue（#155）同时补全了此前断裂的 **org claim 链路**（`DemoDbContext` 读 `"org"` claim 但无人写 —— ghost-claim bug）。

---

## 架构

```
业务领域事件 : INotifyClient
        │  (经 IDomainEventDispatcher 派发)
        ▼
NotificationDispatcher<TEvent>        ← 开放泛型 IDomainEventHandler<TEvent>，DI 按 TEvent 自动构建
        │  (读 evt.Target)
        ▼
IRealtimeNotifier  (HubBasedRealtimeNotifier)
        │
   ┌────┴──────────┐
   ▼               ▼
本地直推          IRealtimeBackplane.PublishAsync
(IHubContext)          │
                       ▼  (其他副本)
        RealtimeBackplaneSubscriber (IHostedService)
                       │  本地直推，不再回广播（防回环）
                       ▼
        IHubContext<NotificationHub>
```

所有代码位于 `TenE0.Core.Realtime`，DI / 端点扩展在 `TenE0.Core.DependencyInjection.RealtimeExtensions`。

---

## 快速开始

```csharp
// Program.cs
builder.Services.AddTenE0Realtime();
builder.Services.AddRealtimeHubTokenFromQuery();   // 让 WS 握手从 ?access_token= 取 JWT

// ... app.UseAuthentication(); app.UseAuthorization(); ...

app.MapTenE0Hub();   // 注册 /hub/notification
```

### 业务方：声明式触发（推荐）

```csharp
public sealed record OrderApprovedEvent(
    string OrderId, string ApproverCode) : INotifyClient
{
    public NotificationTarget Target =>
        NotificationTarget.User(ApproverCode, "order.approved", new { OrderId });
}

// 事件一经 dispatch，ApproverCode 的前端就收到 order.approved
```

零样板：无需手写 handler、无需注入 `IRealtimeNotifier`、无需关心 Hub / backplane。

### 业务方：直接推送（绕过事件，如长任务进度 / 外部回调）

```csharp
public sealed class LongTask(IRealtimeNotifier notifier)
{
    public async Task RunAsync(string userCode, CancellationToken ct)
    {
        for (var i = 0; i < 100; i += 10)
        {
            await notifier.NotifyUserAsync(userCode, "task.progress", new { Percent = i }, ct);
            // ...
        }
    }
}
```

### 前端

```js
const conn = new signalR.HubConnectionBuilder()
  .withUrl("/hub/notification", { accessTokenFactory: () => jwt })
  .build();
conn.on("order.approved", envelope => {
  // envelope.Event, envelope.Data, envelope.TraceId
});
await conn.start();
```

> 浏览器无法在 WebSocket 握手设置 Authorization 头，token 经 `?access_token=` 传递（`AddRealtimeHubTokenFromQuery` 配置 JwtBearer 的 `OnMessageReceived` 提取）。

---

## 投放范围

`NotificationTarget` 三种范围（工厂方法）：

| 方法 | 推给 | Hub 调用 |
|---|---|---|
| `NotificationTarget.User(code, name, payload)` | 指定用户（JWT sub） | `Clients.User(code)` |
| `NotificationTarget.Group(group, name, payload)` | 指定组（如 `org:HQ`） | `Clients.Group(name)` |
| `NotificationTarget.All(name, payload)` | 所有已连接客户端 | `Clients.All` |

消息信封 `NotificationEnvelope` 固定结构：`Event`（事件名）/ `Data`（payload）/ `TraceId`（与审计日志 / APM 关联）。

---

## 组体系

连接建立时，`ClaimBasedGroupProvider` 从 JWT claims 零 I/O 派生连接所属组：

| 组 | claim | 用途 |
|---|---|---|
| `user:{sub}` | `sub` | 推给"自己" |
| `role:{role}` | `role`（多值） | 按角色广播 |
| `tenant:{tenant_id}` | `tenant_id` | 按租户广播（可空，见 [20-multi-tenancy](20-multi-tenancy.md)） |
| `org:{org}` | `org` | 按组织广播（org 节点 Id；可空） |

缺省的 claim（未绑定组织 / 系统账号）安全跳过，不产出空名组。

### org ≠ tenant（重要）

- **tenant**：扁平分区，由 [multi-tenancy](20-multi-tenancy.md) 的 EF Tenant Filter 行级隔离。
- **org**：[组织树](13-organizations.md)，**全局树**（`TenE0Org` 不含 `TenantId`、不实现 `IMultiTenantEntity`），二者**正交** —— 一个 org 节点不属于任何 tenant，一个用户可同时归属某 tenant 与某 org 节点。
- **org claim 链路**：`TenE0User.OrgId` → 登录/刷新写入 `"org"` claim → realtime `ClaimBasedGroupProvider` 派生 `org:{orgId}` 组 / 业务行级 filter 读回隔离。本 issue 补全此前断裂的写入端（`DemoDbContext` 此前读 `org` claim 但无人写）。

### 自定义组（如 `project:{id}`）

实现并替换 `IRealtimeGroupProvider`：

```csharp
public sealed class ProjectAwareGroupProvider : IRealtimeGroupProvider
{
    public IReadOnlyList<string> GetGroups(ClaimsPrincipal user)
    {
        // 返回 user 应加入的组（可查 DB）。Hub 的 Context.User 已验签。
        return ["project:proj-1"];
    }
}
services.Replace(ServiceDescriptor.Singleton<IRealtimeGroupProvider, ProjectAwareGroupProvider>());
```

> ⚠️ Hub 上下文中 `IHttpContextAccessor.HttpContext` 为 null（WS 握手后），实现必须基于传入的 `ClaimsPrincipal`（即 `Context.User`）读取，不能注入 `ICurrentUserContext` / `ITenantContext`。

---

## 多副本部署（backplane）

单体 / 开发用 `NoopRealtimeBackplane`（直推，无广播）。多副本需 Redis（或其它 pub/sub）backplane，让"目标客户端可能连在任意副本"时消息只投递一次：

- 本副本发的消息先本地直推，再 `PublishAsync` 广播给其他副本；
- 其他副本经 `RealtimeBackplaneSubscriber` 收到后本地直推，**不再回广播**（防回环）。

```csharp
builder.Services.AddTenE0Realtime(o => o.Backplane = BackplaneMode.Redis);
// 并提供 IRealtimeBackplane 的 Redis 实现（services.Replace(...)）
```

> Redis backplane 实现留后续 issue。当前选 `Redis` 但未注入实现会抛 `InvalidOperationException`。

---

## API 速查

| 类型 | 说明 |
|---|---|
| `INotifyClient` | 声明式标记接口（继承 `IDomainEvent`），实现即自动推送 |
| `NotificationTarget` | 投放目标（User/Group/All + 事件名 + payload + traceId） |
| `IRealtimeNotifier` | 推送门面：NotifyUser / NotifyGroup / NotifyAll |
| `IRealtimeGroupProvider` | 连接 → 组映射（默认 `ClaimBasedGroupProvider`） |
| `IRealtimeBackplane` | 跨实例广播抽象（默认 `NoopRealtimeBackplane`） |
| `NotificationHub` | 投递通道（`[Authorize]`，OnConnected 加组） |
| `NotificationDispatcher<TEvent>` | 声明式触发桥接（开放泛型 handler） |
| `HubBasedRealtimeNotifier` | 默认 notifier，本地直推 + backplane 广播 |
| `RealtimeBackplaneSubscriber` | IHostedService，订阅远端消息本地直推 |
| `ClaimBasedUserIdProvider` | `IUserIdProvider`，sub claim → `Context.UserIdentifier`（让 `Clients.User` 生效） |
| `RealtimeOptions` | HubPath（默认 `/hub`）/ Backplane / GroupPrefixes |

### DI / 端点扩展

| 方法 | 作用 |
|---|---|
| `AddTenE0Realtime(configure?)` | 注册 Hub + notifier + 组/用户映射 + backplane + 声明式 dispatcher + 订阅 hostedservice |
| `AddRealtimeHubTokenFromQuery()` | 配置 JwtBearer 从 `?access_token=` 提取 token（WS 握手用） |
| `MapTenE0Hub(hubPath?)` | 注册 `{hubPath}/notification` 端点 |

---

## 测试

- **单元**（`10E0.Core.Tests/Realtime/`）：Notifier 直推 + backplane、GroupProvider 组派生、UserIdProvider、Dispatcher 三种投放范围、Extensions 服务注册、org claim round-trip（`Auth/Jwt/OrgClaimJwtAcceptanceTests`）。
- **集成**（`10E0.Api.Tests/RealtimeHubAcceptanceTests`）：Hub 连接鉴权（无 token 被拒 / query-string token 通过）、`Clients.User` 定向推送、`Clients.Group("org:{nodeId}")` 端到端（验证 org claim 链路从 seeder 到前端全程通）。
