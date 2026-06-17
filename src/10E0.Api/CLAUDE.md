# 10E0.Api — HTTP API 层

应用入口项目，使用 **Minimal API** 风格（无 Controller 基类、无 Startup 类）。

## 文件说明

| 文件 | 职责 |
|------|------|
| `Program.cs` | 应用入口：DI 注册 + 调用各 `Map*Endpoints` 扩展 + `app.Run()` |
| `Domain/` | 业务领域类型：`AppUser` / `DemoDbContext` / `DemoEntity` / `DemoPermissions` / `DemoPermissionProvider` / `DemoOrgScopedFilter` |
| `Endpoints/HealthEndpoints.cs` | `/` 健康检查 |
| `Endpoints/AuthEndpoints.cs` | `/auth/login` `/auth/refresh` `/auth/logout` |
| `Endpoints/DemoEndpoints.cs` | `/whoami` `/demo/*` CRUD + 动态查询 + 部分更新演示 |
| `Endpoints/AdminEndpoints.cs` | `/admin/*` 后台管理（组织、Outbox、权限、菜单、数据过滤规则）；`RequireAdminAttribute` 在此 |
| `Endpoints/FileEndpoints.cs` | `/files/*` 文件上传/下载/元数据 |
| `Handlers/` | `DemoCommands` / `*DemoCommandHandler` / `ListDemosQueryHandler` / `DemoEventTrigger` / `DemoFieldPermissions` |
| `Events/DemoEvents.cs` | `DemoCreatedEvent` / `DemoPublishedEvent` |
| `Events/DemoEventHandlers.cs` | 三个日志订阅者：`DemoCreatedAuditHandler` / `DemoPublishedNotificationHandler` / `DemoPublishedAuditHandler` |
| `Seeders/PermissionSeeder.cs` | 启动时初始化角色 + 默认 grants |
| `Seeders/AuthSeeder.cs` | 启动时初始化 admin/alice 账号 + 一棵示例组织树 |
| `MenuSeeder.cs` | 菜单种子数据：Dashboard + 系统管理（用户/角色/菜单） |
| `Hosting/DynamicFilterBootstrap.cs` | 启动时根据 EF provider 决定是否加载动态过滤规则 |
| `Hosting/NullUserInfoLoader.cs` | 空实现的 `IUserInfoLoader`（demo 不接外部 IdP） |
| `Properties/launchSettings.json` | VS Code 启动配置 |

## Program.cs 中定义的关键类型

| 类型 | 位置 | 说明 |
|------|------|------|
| `AppUser : TenE0User` | `Domain/AppUser.cs` | 扩展用户实体（Avatar, Department, Birthday） |
| `DemoDbContext : TenE0SystemDbContext<AppUser, TenE0Role>` | `Domain/DemoDbContext.cs` | 应用 DbContext |
| `DemoEntity : AggregateRoot` | `Domain/DemoEntity.cs` | 演示聚合根 + 领域事件 |
| `DemoPermissions` / `DemoPermissionProvider` | `Domain/DemoPermissions.cs` | 权限常量定义 + 注册 |
| `DemoOrgScopedFilter : EntityFilterContributor<DemoEntity>` | `Domain/DemoDbContext.cs` | 行级数据过滤演示 |
| `DemoCreatedAuditHandler` / `DemoPublishedNotificationHandler` | `Events/DemoEventHandlers.cs` | 领域事件处理器演示 |

## API 端点

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/` | 健康检查 |
| POST | `/auth/login` | 登录 |
| POST | `/auth/refresh` | 刷新令牌 |
| POST | `/auth/logout` | 登出 |
| GET/POST/PUT/DELETE | `/demo/*` | CRUD 演示 |
| POST | `/demo/query` | 动态查询演示（WHERE/ORDER BY/分页） |

## 注意事项

- 这是 **演示项目**，不是生产 API。真实业务应新建独立项目引用 10E0.Core
- `Program.cs` 已 < 100 行，详见上方"文件说明"
- 种子数据通过 `DatabaseInitializerService` 在启动时执行
- 各 `Map*Endpoints` 扩展独立可组装，可作为新项目模板复用
