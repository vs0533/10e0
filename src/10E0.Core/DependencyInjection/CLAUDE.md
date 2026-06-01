# DependencyInjection/ — DI 注册扩展方法

每个框架模块提供独立的 `IServiceCollection` 扩展方法，支持按需组合。

## 文件说明

| 文件 | 扩展方法 | 注册内容 |
|------|----------|----------|
| `ServiceCollectionExtensions.cs` | `AddTenE0Core()` | 核心：HttpContextAccessor、TimeProvider、DistributedCache、ICurrentUserContext、IErrs、IDataAccessPolicy、AuditInterceptor、DbContext 工厂 |
| `CqrsServiceCollectionExtensions.cs` | `AddTenE0Cqrs()` | CommandDispatcher + LoggingBehavior + 程序集扫描 ICommandHandler |
| `IdentityExtensions.cs` | `AddTenE0Identity<TUser, TRole, TContext>()` | 一站式注册：JWT + 权限 + 组织 |
| `JwtAuthExtensions.cs` | `AddTenE0JwtAuth<TUser>()` | TokenService、PasswordHasher、3 个命令处理器、JWT Bearer 配置 |
| `PermissionsExtensions.cs` | `AddTenE0Permissions()` | PermissionEvaluator、PermissionCache、PermissionCatalog 扫描、PermissionBehavior |
| `EntityServiceExtensions.cs` | `AddTenE0EntityService()` | EntityService 注册 |
| `MenusExtensions.cs` | `AddTenE0Menus()` | MenuService 注册 |
| `OrganizationsExtensions.cs` | `AddTenE0Organizations()` | OrgTreeService 注册 |
| `SequencesExtensions.cs` | `AddTenE0Sequences()` | SequenceGenerator 注册 |
| `DomainEventsExtensions.cs` | `AddTenE0DomainEvents()` | DomainEventDispatcher + 程序集扫描 IDomainEventHandler |
| `DynamicFiltersExtensions.cs` | `AddTenE0DynamicFilters()` | DynamicFilterProvider + DataFilterRuleService |

## 设计决策

- **对比旧 `AddE0Context()`**：旧版一个方法注册所有东西，无法按需裁剪。新版每个模块独立注册
- **`AddTenE0Identity<>()`** 是"一站式"快捷方法，内部调用 JWT + 权限 + 组织的各扩展
- Handler 和 DomainEventHandler 通过程序集扫描自动注册，无需手动配置
