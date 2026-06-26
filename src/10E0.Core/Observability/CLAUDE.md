# Observability — 可观测性模块（#161）

健康检查（HealthChecks）+ 指标（Metrics）+ OTel 追踪接入点。**零新依赖** ——
HealthChecks（`IHealthCheck`/`AddHealthChecks`/`MapHealthChecks`）与 Metrics
（`System.Diagnostics.Metrics`：`Meter`/`Counter`/`Histogram`/`ObservableGauge`）都在
`Microsoft.AspNetCore.App` 共享框架内。OTel SDK / OTLP / Prometheus 导出器由 **app 层** 按需引用
（见 `docs/26-observability.md`），契合本仓库 `IDbProviderConfigurator` 反膨胀 SPI 惯例，发布包不增重。

## 目录结构

| 文件 | 职责 |
|------|------|
| `ObservabilityOptions.cs` | 配置：服务名 / OTLP 端点 / 健康检查超时 / Outbox 积压阈值 |
| `TenE0Metrics.cs` | DI Singleton 持有 `Meter("TenE0")` + 4 个仪器（CQRS 计数/耗时 + Outbox 投递/积压） |
| `HealthChecks/DbContextHealthCheck.cs` | `AnyAsync()` 探测（关系型 + InMemory 通用） |
| `HealthChecks/OutboxHealthCheck.cs` | 积压数阈值判定（Healthy/Degraded/Unhealthy） |
| `HealthChecks/FileStorageHealthCheck.cs` | `IFileStorage` 写读删往返（仅 Files 启用时挂） |

注册扩展在 `../DependencyInjection/ObservabilityExtensions.cs`：
`AddTenE0Observability<TContext>()` + `MapTenE0HealthChecks(adminAuthorizationPolicy)`。

## 关键设计决策

1. **Meter 用 DI Singleton 而非 static**（偏离 issue 草案的 `static` 设计）—— 契合"每个服务独立注入、可测试、不 ServiceLocator"原则。无读取者时仪器近零开销，未注册 `TenE0Metrics` 时埋点 no-op。
2. **埋点 null-safe**：`CommandDispatcher` / `OutboxRelayService` 用 `serviceProvider.GetService<TenE0Metrics>()` 解析，未启用 Observability 时为 `null` → 不影响热路径。
3. **`DbContextHealthCheck` 用 `AnyAsync()` 而非 `AddDbContextCheck`/`SELECT 1`** —— `SELECT 1` 在 InMemory provider 不适用；探测框架内置的 `OutboxMessage` 表验证连接 + 元数据可用。
4. **Outbox 积压口径与 Relay 一致**：`SentTime == null && AttemptCount < MaxAttempts`（排除已超 `MaxAttempts` 的毒消息，否则计数虚高）。`MaxAttempts` 来自 `OutboxRelayOptions`（DomainEvents 启用时注入），未启用时回退默认值 8。
5. **健康端点三层**：`/health/live`（匿名，恒 200）/ `/health/ready`（匿名，跑 `ready` 标签的检查）/ `/health`（完整 JSON 报告，需鉴权）。live/ready 必须**匿名** —— K8s 探针不携带认证。
6. **Core 不引用 Api 的 `[RequireAdmin]`** —— `/health` 的鉴权由调用方传策略名（`adminAuthorizationPolicy`），保持 Core 不依赖业务权限定义。
7. **FileStorage 健康检查条件挂载**：仅当 `IFileStorage` 已注册（Files 模块启用）才挂，避免对未启用文件功能的项目误报 Unhealthy。

## 协同

- HealthChecks 自动纳入 `OutboxMessage`（领域事件 #155 SignalR backplane 等）与 `OutboxMessage` 表的依赖检查。
- Metrics 埋点在 `Cqrs/CommandDispatcher.cs` 与 `Events/Outbox/OutboxRelayService.cs`。
- 后续 issue（审计 #152、工作流 #157-#159）可追加各自的 check 与指标。
