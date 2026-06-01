# 10E0.Api — HTTP API 层

应用入口项目，使用 **Minimal API** 风格（无 Controller 基类、无 Startup 类）。

## 文件说明

| 文件 | 职责 |
|------|------|
| `Program.cs` | 全部应用逻辑：DI 注册、路由定义、种子数据、Demo CRUD |
| `MenuSeeder.cs` | 菜单种子数据：Dashboard + 系统管理（用户/角色/菜单） |
| `Properties/launchSettings.json` | VS Code 启动配置 |

## Program.cs 中定义的关键类型

| 类型 | 说明 |
|------|------|
| `AppUser : TenE0User` | 扩展用户实体（Avatar, Department, Birthday） |
| `DemoDbContext : TenE0SystemDbContext<AppUser, TenE0Role>` | 应用 DbContext |
| `DemoEntity : AggregateRoot` | 演示聚合根 + 领域事件 |
| `DemoPermissions` | 权限常量定义 |
| `DemoPermissionProvider : IPermissionProvider` | 权限注册 |
| `DemoOrgScopedFilter : EntityFilterContributor<DemoEntity>` | 行级数据过滤演示 |
| `DemoCreatedAuditHandler` / `DemoPublishedNotificationHandler` | 领域事件处理器演示 |

## API 端点

| 方法 | 路径 | 说明 |
|------|------|------|
| POST | `/auth/login` | 登录 |
| POST | `/auth/refresh` | 刷新令牌 |
| POST | `/auth/logout` | 登出 |
| GET/POST/PUT/DELETE | `/demo/*` | CRUD 演示 |
| POST | `/demo/query` | 动态查询演示（WHERE/ORDER BY/分页） |

## 注意事项

- 这是 **演示项目**，不是生产 API。真实业务应新建独立项目引用 10E0.Core
- `Program.cs` 超 1000 行，生产环境应拆分为独立模块
- 种子数据通过 `DatabaseInitializerService` 在启动时执行
