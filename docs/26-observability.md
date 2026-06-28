# 26 — 可观测性（Observability）

生产部署的两大刚需一次补齐：**健康检查**（HealthChecks，K8s liveness/readiness 探针可消费）+ **指标**（Metrics，CQRS / Outbox 关键指标，Prometheus 格式）+ **OpenTelemetry 追踪**（自动 instrument HTTP / EF Core，导出到 OTLP）。

**设计取舍**：Core 自身**零新依赖** —— HealthChecks 与 `System.Diagnostics.Metrics` 都在 `Microsoft.AspNetCore.App` 共享框架内；OpenTelemetry SDK（追踪 / OTLP / Prometheus 导出器）由 **app 层** 按需引用，契合本仓库 `IDbProviderConfigurator` 反膨胀 SPI 惯例，发布的 `TenE0.Core` NuGet 包不增重。

所有框架代码位于 `TenE0.Core.Observability`，DI / 端点扩展在 `TenE0.Core.DependencyInjection.ObservabilityExtensions`。

---

## 架构

```
                  ┌─────────────────────────────────────────┐
  K8s liveness ──▶│ /health/live   匿名恒 200（不跑检查）        │
  K8s readiness ─▶│ /health/ready  匿名，跑 "ready" 标签的检查   │
  运维 / Grafana─▶│ /health        完整 JSON 报告（需 perm.admin）│
                  └─────────────────────────────────────────┘
                                  │
        HealthChecks ─────────────┼────────────────────
        DbContextHealthCheck      │  AnyAsync() 探测（关系型 + InMemory 通用）
        OutboxHealthCheck         │  积压数阈值 → Healthy/Degraded/Unhealthy
        FileStorageHealthCheck    │  写读删往返（仅 Files 启用时挂）

        Metrics（System.Diagnostics.Metrics，Meter("TenE0")）
        CommandDispatcher.SendAsync     ▶ tene0.command.total / .duration
        OutboxRelayService.ProcessBatch ▶ tene0.outbox.delivered / .backlog

        app 层（OTel SDK，按需）
        AddOpenTelemetry().WithMetrics(...).WithTracing(...)
          ▼
        Prometheus /metrics 端点 + OTLP → Collector → Grafana / Jaeger / Datadog
```

---

## 快速开始

### 1. 启用 Observability（Core 侧，一行）

```csharp
// Program.cs —— 在 AddTenE0All 的 options 里 opt-in
builder.Services.AddTenE0All<AppUser, DemoDbContext>(builder.Configuration, opt =>
{
    // ...
    opt.Observability = true;
    opt.ObservabilityOptions = obs =>
    {
        obs.ServiceName = "MyApp.Api";
        obs.OtlpEndpoint = builder.Configuration["OTEL:Endpoint"]; // null = 不导出追踪
        obs.OutboxDegradedThreshold = 100;
        obs.OutboxUnhealthyThreshold = 1000;
    };
});
```

这一步注册 `TenE0Metrics`（DI Singleton，埋点用）+ HealthChecks（DbContext / Outbox / 可选 FileStorage）。

### 2. 挂载健康端点

```csharp
// Program.cs —— 在 UseAuthorization 之后
// 传 admin 策略名让 /health 完整报告需鉴权；live/ready 始终匿名（K8s 探针不认证）。
app.MapTenE0HealthChecks(adminAuthorizationPolicy: PermissionPolicies.Admin);
```

### 3. （可选）app 层装配 OTel SDK + Prometheus

```bash
# app 项目引用 OTel 包（Core 不带这些依赖）
dotnet add package OpenTelemetry.Extensions.Hosting
dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol
dotnet add package OpenTelemetry.Instrumentation.AspNetCore
dotnet add package OpenTelemetry.Instrumentation.Http
dotnet add package OpenTelemetry.Instrumentation.EntityFrameworkCore   # 当前仅 prerelease
dotnet add package OpenTelemetry.Exporter.Prometheus.AspNetCore         # 当前仅 prerelease
```

```csharp
var otel = builder.Services.AddOpenTelemetry();
otel.ConfigureResource(r => r.AddService("MyApp.Api"));

// Metrics：自定义 Meter + 框架 instrument + Prometheus 导出
otel.WithMetrics(m => m
    .AddMeter(TenE0Metrics.MeterName)              // 订阅 tene0.* 指标
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    .AddPrometheusExporter());

// Tracing：仅配置了 OTLP 端点时启用
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL:Endpoint"]))
{
    otel.WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddSource(TenE0Metrics.MeterName)
        .AddOtlpExporter());
}

// Prometheus 抓取端点（需鉴权，避免内部指标外泄）
app.MapPrometheusScrapingEndpoint("/metrics")
   .RequireAuthorization(PermissionPolicies.Admin);
```

> 10E0.Api demo 项目已内置上述完整装配，可直接参考 `src/10E0.Api/Program.cs`。

---

## 健康检查

| 端点 | 鉴权 | 行为 |
|------|------|------|
| `GET /health/live` | 匿名 | 进程存活，恒 200（K8s liveness） |
| `GET /health/ready` | 匿名 | 跑 `ready` 标签检查，全 Healthy 才 200（K8s readiness） |
| `GET /health` | `perm.admin` | 完整 JSON 报告（每项 check 状态 / 耗时 / 数据） |

三个内置 HealthCheck（均带 `ready` 标签）：

| Check | 探测方式 | 异常状态 |
|-------|---------|---------|
| `DbContextHealthCheck<TContext>` | `AnyAsync()` 探测 `OutboxMessage` 表（关系型 + InMemory 通用） | 连接失败 → `Unhealthy` |
| `OutboxHealthCheck<TContext>` | 积压数 `SentTime==null && AttemptCount<MaxAttempts` | `≥Degraded阈值` → Degraded；`≥Unhealthy阈值` → Unhealthy |
| `FileStorageHealthCheck` | `IFileStorage` 写读删往返（仅 Files 启用时挂） | 写入失败 → `Unhealthy` |

> `AddDbContextCheck`（需独立包）默认走 `SELECT 1`，在 InMemory provider 不适用；故自建 `AnyAsync()` 探测。

---

## Metrics 指标清单

Meter 名 `TenE0`（`TenE0Metrics.MeterName`）。Prometheus 命名前缀 `tene0_`。

| 指标 | 类型 | tags | 说明 | 埋点位置 |
|------|------|------|------|---------|
| `tene0.command.total` | Counter | `command`、`result=success\|failure` | CQRS 命令计数 | `CommandDispatcher.SendAsync` |
| `tene0.command.duration` | Histogram(ms) | `command` | CQRS 命令耗时 | `CommandDispatcher.SendAsync` |
| `tene0.outbox.delivered` | Counter | `result=success\|failure` | Outbox 投递结果 | `OutboxRelayService.ProcessBatchAsync` |
| `tene0.outbox.backlog` | ObservableGauge | — | Outbox 待投递积压 | `OutboxRelayService.ProcessBatchAsync`（每轮刷新） |

**埋点 null-safe**：`CommandDispatcher` / `OutboxRelayService` 通过 `serviceProvider.GetService<TenE0Metrics>()` 解析；未启用 Observability 时为 `null` → no-op，零热路径影响。

---

## 配置参考

```json
{
  "Observability": {
    "ServiceName": "MyApp.Api",
    "OtlpEndpoint": "http://otel-collector:4317",
    "EnableHealthChecks": true,
    "HealthCheckTimeout": "00:00:03",
    "OutboxDegradedThreshold": 100,
    "OutboxUnhealthyThreshold": 1000
  }
}
```

或环境变量覆盖：`Observability__OtlpEndpoint=http://otel-collector:4317`。

---

## 设计决策

1. **Core 零新依赖**：HealthChecks + `System.Diagnostics.Metrics` 在共享框架内；OTel SDK 下沉 app 层，避免框架包膨胀。
2. **Meter 用 DI Singleton 而非 static**：契合"每个服务独立注入、可测试"原则；无读取者时近零开销。
3. **Outbox 积压口径与 Relay 一致**：`SentTime==null && AttemptCount<MaxAttempts`，排除毒消息避免计数虚高。
4. **live/ready 匿名**：K8s 探针不携带认证；`/health` 完整报告与 `/metrics` 才需 `perm.admin`。
5. **Core 不引用 Api 的 `[RequireAdmin]`**：`MapTenE0HealthChecks` 接收策略名参数，保持 Core 不依赖业务权限定义。
6. **FileStorage 健康检查条件挂载**：仅 `IFileStorage` 已注册（Files 启用）时挂，避免对未启用文件功能的项目误报。

## 协同

- HealthChecks 自动纳入 `OutboxMessage` 表依赖检查；后续 issue（审计 #152、工作流 #157-#159）可追加各自 check。
- Metrics 埋点在 CQRS / Outbox 热路径，业务命令 / 工作流命令自动纳入 `tene0.command.*`。
