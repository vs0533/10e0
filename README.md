# 10E0 (TenE0) — 下一代企业低代码框架

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![NuGet](https://img.shields.io/nuget/v/TenE0.Core)](https://www.nuget.org/packages/TenE0.Core/)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)
[![Build](https://img.shields.io/github/actions/workflow/status/vs0533/10e0/pr-build.yml?branch=dev)](https://github.com/vs0533/10e0/actions)

10E0 是一个基于 Clean Architecture + DDD + CQRS 的企业级低代码开发框架，目标框架 .NET 10 / C# 14。它帮你摆脱 MediatR 的商业许可限制和笨重的样板代码，用自建分发器、可组合管道和声明式权限模型，几行代码就能搭好一套生产级后端。

## 核心特性

- **自建 CQRS 分发器** — 不依赖 MediatR，无商业许可风险，支持 Command / Query / Event 三种消息类型
- **Pipeline Behavior 管道链** — Logging → Transaction → Permission → Handler，类 ASP.NET Core 中间件模式，行为可插拔
- **EF Core 10 + IDbContextFactory** — 作用域工厂模式，支持并发查询；Savepoint 替代嵌套事务，修复经典 Bug
- **Outbox Pattern 领域事件** —
  - 领域事件同事务落库，后台 Relay 异步发布（最终一致性）
  - **4 种 Lock Provider** (`None` / `RowLock` / `Distributed` / `Leader`) 跨实例 exactly-once
  - `OutboxSchemaSeeder` 启动期幂等迁移 `LockedUntil` / `LockedByInstance` 列
  - `IOutboxAdmin` 死信管理：查询 / 重试 / 导出三操作
- **多租户（Multi-Tenancy）** — `IMultiTenantEntity` + Named Query Filter 自动隔离；`ITenantContext` 从 JWT `tenant_id` claim 注入；超管 `BypassFilters` 短路
- **角色版本号（instant permission revocation）** — `IRoleVersionStore` + JWT `role_versions` claim；撤销某用户权限后**下一个 HTTP 请求立即 403**，无需等待 token 过期（关 #7 安全 HIGH-4）
- **RBAC 权限系统** — 功能权限 + 字段级权限 + 行级数据过滤三层粒度，Permission Key 驱动的声明式模型
- **多级缓存抽象** — `IMultiLevelCache` L1 (进程内) + L2 (分布式) + `IAtomicCounter` (Redis `INCR` / 内存 `Interlocked.Increment` / EF `UPDATE OUTPUT`)；纯读 `GetAsync` API + SETNX 进程内锁防 exactly-once 失败
- **动态数据过滤** — 运行时 JSON 规则引擎，无需改代码即可实现复杂数据隔离
- **文件服务** — 统一抽象，支持本地存储、Aliyun OSS、AWS S3，开箱即用的图片处理
- **导入导出** — 通用 Excel(ClosedXML)/CSV(RFC 4180) 导入导出 + 声明式列映射 + `ImportExecutor` 走 `EntityService` 校验链 + 大文件流式/降级
- **组织架构与菜单管理** — 物化路径树实现组织树和菜单树，支持角色分配
- **动态查询与分页** — 参数化查询，防 SQL 注入，支持排序、过滤、字段选择
- **定时任务调度** — Cronos 驱动的 Cron 调度框架（`[Scheduled]` 声明式静态任务 + 动态任务 API）、持久化调度、多实例集群行级锁、失败重试、执行历史、可观测性埋点（#164）

## 快速开始

```csharp
var connectionString = builder.Configuration.GetConnectionString("Default");

builder.Services.AddTenE0Core();
builder.Services.AddTenE0DataContext<AppDbContext>((_, opt) =>
    opt.UseSqlServer(connectionString));
builder.Services.AddTenE0Cqrs(typeof(Program).Assembly);
builder.Services.AddTenE0Identity<AppDbContext>(opt =>
{
    // ⚠️ 生产环境从配置/环境变量读取
    opt.Jwt.SigningKey = builder.Configuration["Jwt:SigningKey"]
        ?? throw new InvalidOperationException("Jwt:SigningKey 未配置");
    // 多租户 (#11)：开启 Tenant claim 解析 → EF Named Query Filter 自动按租户隔离
    opt.Jwt.TenantClaimName = "tenant_id";
    // 角色版本号 (#7)：签发 JWT 时把 {roleCode: version} 快照写入 role_versions claim
});
builder.Services.AddTenE0Outbox<AppDbContext>(opt =>
{
    opt.BatchSize = 50;
    opt.MaxAttempts = 8;
    opt.LockProvider = OutboxLockProviderKind.RowLock;  // 0/1 实例可省（None）
});
var app = builder.Build();
app.UseAuthentication(); app.UseAuthorization();
app.Run();
```

多租户详细启用方式见 [docs/20-multi-tenancy.md](docs/20-multi-tenancy.md)；Outbox 多实例 Lock Provider 选型见 [docs/10-domain-events.md §10.5.1](docs/10-domain-events.md)。

## 文档

| 文档 | 说明 |
|------|------|
| [架构概览](docs/01-architecture.md) | 整体架构设计 |
| [快速开始](docs/02-quickstart.md) | 5 分钟上手 |
| [DI 注册指南](docs/03-di-setup.md) | 依赖注入完整参考 |
| [CQRS](docs/04-cqrs.md) | 命令查询分离 |
| [实体模型](docs/05-entities.md) | 实体基类与继承链 |
| [EntityService](docs/06-entity-service.md) | 通用 CRUD 服务 |
| [DataContext](docs/07-data-context.md) | EF Core 配置 |
| [JWT 认证](docs/08-auth-jwt.md) | 登录 / 刷新 / 登出 |
| [权限系统](docs/09-permissions.md) | RBAC + 字段 + 行级 |
| [领域事件](docs/10-domain-events.md) | Outbox Pattern |
| [动态过滤](docs/11-dynamic-filters.md) | 运行时数据规则 |
| [文件服务](docs/12-files.md) | 上传 / 下载 / 图片处理 |
| [组织架构](docs/13-organizations.md) | 物化路径树 |
| [菜单管理](docs/14-menus.md) | 菜单树与角色分配 |
| [流水号](docs/15-sequences.md) | 自动编号生成 |
| [动态查询](docs/16-dynamic-queries.md) | 参数化查询与分页 |
| [部署](docs/17-deployment.md) | CI/CD 与发版 |
| [同步 PR 策略](docs/18-sync-pr-strategy.md) | dev → main 合并为何禁 Squash |
| [同步事故复盘](docs/19-sync-retrospective.md) | 2026-06 同步事故复盘 |
| [多租户](docs/20-multi-tenancy.md) | `IMultiTenantEntity` + Named Query Filter |
| [审批流](docs/21-workflow.md) | 状态机 + 流程定义 + 运行时 |
| [导入导出](docs/22-import-export.md) | Excel/CSV 导入导出（ClosedXML + ImportExecutor） |
| [实时推送](docs/23-realtime.md) | 声明式 SignalR（INotifyClient） |
| [API 版本化](docs/24-api-versioning.md) | Asp.Versioning + 版本透明 + 每版本 OpenAPI |
| [文档索引](docs/index.md) | 全文档分类导航 |

## 构建与运行

```bash
dotnet build 10e0.slnx
dotnet test 10e0.slnx
dotnet run --project src/10E0.Api
```

## NuGet

```bash
dotnet add package TenE0.Core
```

## 项目结构

```
src/
├── 10E0.Api/    — HTTP API 层（Minimal API，应用入口 + Demo）
└── 10E0.Core/   — 共享框架核心（NuGet 包: TenE0.Core）

tests/
├── 10E0.Api.Tests/     — API 集成测试（xUnit + WebApplicationFactory）
└── 10E0.Core.Tests/    — Core 单元测试（xUnit + EF Core InMemory + coverlet）
```

## 许可证

MIT

