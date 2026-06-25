# Realtime 实时推送模块（#155）

声明式 SignalR 推送：业务领域事件实现 `INotifyClient` 即自动推送给前端，零样板。

## 架构

```
业务事件 : INotifyClient
        │ (经 IDomainEventDispatcher 派发)
        ▼
NotificationDispatcher<TEvent>   ← 开放泛型 IDomainEventHandler<TEvent>，DI 按事件类型自动构建
        │ (读 evt.Target)
        ▼
IRealtimeNotifier (HubBasedRealtimeNotifier)
        │
   ┌────┴─────┐
   ▼          ▼
本地直推    IRealtimeBackplane.PublishAsync
(IHubContext)   │
                ▼ （其他副本）
        RealtimeBackplaneSubscriber (IHostedService)
                │ 本地直推，不再回广播（防回环）
                ▼
        IHubContext<NotificationHub>
```

## 关键文件

- `Abstractions/INotifyClient.cs` — 声明式标记接口 + `NotificationTarget`（User/Group/All）。事件实现它即被自动推送。
- `Abstractions/IRealtimeNotifier.cs` — 推送门面：NotifyUser/Group/All。绕过事件直接推送时用。
- `Abstractions/IRealtimeGroupProvider.cs` — 连接→组映射。默认 `ClaimBasedGroupProvider` 从 claims 派生。
- `Abstractions/IRealtimeBackplane.cs` — 跨实例广播抽象。单体用 `NoopRealtimeBackplane`，Redis 留后续。
- `NotificationHub.cs` — 薄 Hub：OnConnected 加组，OnDisconnected 自动清组。
- `NotificationDispatcher.cs` — 声明式触发核心（开放泛型 handler）。
- `HubBasedRealtimeNotifier.cs` — 默认 notifier，本地直推 + backplane 广播。
- `RealtimeBackplaneSubscriber.cs` — IHostedService，订阅 backplane 远端消息本地直推。
- `ClaimBasedUserIdProvider.cs` — IUserIdProvider，sub claim → Context.UserIdentifier（让 Clients.User(code) 生效）。
- `RealtimeOptions.cs` — HubPath / Backplane / GroupPrefixes。

## 组体系（默认，从 JWT claims 零 I/O 派生）

| 组 | 来源 claim | 用途 |
|---|---|---|
| `user:{sub}` | sub | 推给"自己" |
| `role:{role}` | role（多值） | 按角色广播 |
| `tenant:{tenant_id}` | tenant_id | 按租户广播（可空） |
| `org:{org}` | org | 按组织广播（org 节点 Id；与 tenant 正交） |

**org ≠ tenant**：org 是 Organizations 模块的全局树（`TenE0Org` 不含 TenantId、不实现 IMultiTenantEntity），二者正交。
org claim 链路（`TenE0User.OrgId` → 登录写入 `"org"` claim → realtime/filter 读回）由本 issue 补全（此前 DemoDbContext 读 `org` claim 但无人写，是 ghost-claim bug）。

自定义组（如 `project:{id}`）：业务方替换 `IRealtimeGroupProvider` 实现。

## Hub 上下文约束

Hub 的 `Context.User` 由 JWT bearer 填充（WS 握手前验签）。
**不要**在 Hub / group provider 里注入 `ICurrentUserContext` / `ITenantContext` —— 它们基于 `IHttpContextAccessor`，WS 握手后 `HttpContext` 为 null。统一从 `Context.User`（ClaimsPrincipal）读 claims。

## 用法

### 业务方（声明式，推荐）
```csharp
public sealed record OrderApprovedEvent(string OrderId, string ApproverCode)
    : INotifyClient
{
    public NotificationTarget Target =>
        NotificationTarget.User(ApproverCode, "order.approved", new { OrderId });
}
// 事件一经 dispatch，ApproverCode 的前端就收到 order.approved
```

### Api 接入（Program.cs）
```csharp
builder.Services.AddTenE0Realtime();
// ...
app.MapTenE0Hub(); // 注册 /hub/notification + JWT query-string 认证
```

### 前端
```js
const conn = new signalR.HubConnectionBuilder()
  .withUrl("/hub/notification", { accessTokenFactory: () => jwt })
  .build();
conn.on("order.approved", envelope => { /* envelope.Data, envelope.TraceId */ });
```
