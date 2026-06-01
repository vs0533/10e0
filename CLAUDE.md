# 10E0 (TenE0) — 下一代企业低代码框架

从 `code/E0.Core/` (.NET 6) 重构而来，命名空间 `TenE0.*`，目标框架 .NET 10。

## 项目结构

```
src/
├── 10E0.Api/    — HTTP API 层（Minimal API，应用入口 + Demo）
└── 10E0.Core/   — 共享框架核心（类库）
```

## 架构特征

- **Clean Architecture + DDD + CQRS**（自建 Dispatcher，不依赖 MediatR）
- **Pipeline Behavior 链**：Logging → Transaction → Permission → Handler（类 ASP.NET Core 中间件）
- **EF Core 10 + IDbContextFactory**：作用域工厂模式，支持并发查询
- **Outbox Pattern**：领域事件同事务落库 + 后台 Relay 异步发布
- **Named Query Filters**：软删除和行级数据过滤由 EF Core 自动注入

## 相对旧 E0.Core 的关键改进

| 改进 | 说明 |
|------|------|
| 去掉 MediatR | 自建 ICommandDispatcher，消除 12.x+ 商业许可风险 |
| 去掉 E0Context 大杂烩 | 拆为独立 DI 服务，可组合、可测试 |
| 去掉 MultipleEntity 基类 | M:N 改用 EF Core Skip Navigation 自省 |
| 修复 CommandManager 嵌套事务 Bug | TransactionBehavior 用 Savepoint 替代嵌套事务 |
| 去掉 MetaContext 反射缓存 | 改用 EF Core IModel 元数据 |
| 权限模型重构 | ControllTag+AccessCode → Permission Key + 分布式缓存 |

## 构建

```bash
cd src && dotnet build
```

## 运行

```bash
cd src/10E0.Api && dotnet run
```

## GitHub 工具

- 项目使用 GitHub API MCP Server 管理仓库

使用前确保已配置 GitHub 认证（个人访问令牌），MCP Server 会自动处理鉴权。

## 目录说明

每个子目录都有独立的 `CLAUDE.md`，描述该模块的职责、设计决策和注意事项。
