# tests/ — 测试项目

| 项目 | 引用 | 用途 |
|------|------|------|
| `10E0.Core.Tests` | 10E0.Core | Core 框架核心逻辑单元测试 |
| `10E0.Api.Tests` | 10E0.Api | Api 集成测试（WebApplicationFactory） |

## 运行

```bash
cd /Users/wilder/dev/10e0
dotnet test 10e0.slnx                          # 全部测试
dotnet test tests/10E0.Core.Tests              # 仅 Core 测试
dotnet test tests/10E0.Api.Tests               # 仅 Api 测试
dotnet test --collect:"XPlat Code Coverage"    # 带覆盖率
```

## 技术栈

- **xUnit** — 测试框架
- **coverlet** — 代码覆盖率收集
- **Microsoft.EntityFrameworkCore.InMemory** — EF Core 内存数据库（Core 测试）
- **Microsoft.AspNetCore.Mvc.Testing** — WebApplicationFactory（Api 集成测试）

## 当前状态

- **10E0.Core.Tests**: 535 个单元测试，覆盖 Auth/Jwt、Cqrs、Permissions、Events/Outbox、DynamicFilters、EntityService、Files、Hosting、Json、Menus、Organizations、Queries、Sequences 等 17 个模块
- **10E0.Api.Tests**: 1 个占位测试（程序集加载验证），集成测试待添加
- **CI 覆盖率阈值**: 行 80%（已达标：80.34% 行 / 85.18% 分支 / 83.54% 方法）

### 各模块测试文件

| 模块 | 测试文件 | 用例 |
|------|---------|------|
| Abstractions | EntityContracts, DefaultDataAccessPolicy | 2 |
| Auth | AuthCommands, HttpCurrentUserContext, AmbientCurrentUserContext | 3 |
| Auth/Jwt | JwtTokenService, Pbkdf2PasswordHasher | 2 |
| Auth/Jwt/Commands | LoginCommandHandler, RefreshTokenCommandHandler, LogoutCommandHandler | 3 |
| Auth/Jwt/Storage | AuthModelBuilderExtensions | 1 |
| Common | ApiResult | 1 |
| Cqrs | CommandDispatcher | 1 |
| Cqrs/Behaviors | TransactionBehavior, LoggingBehavior | 2 |
| DataContext/Interceptors | AuditInterceptor | 1 |
| DynamicFilters | FilterExpressionBuilder, ConditionRule, DataFilterRuleService | 3 |
| Entities | BaseEntity | 1 |
| EntityService | Create, Update, Delete, WriteOptions | 4 |
| EntityService/Relations | RelationProcessor | 1 |
| EntityService/Validators | FieldUnique, GroupUnique, UniqueFactory | 3 |
| Errors | Errs | 1 |
| Events | AggregateRoot, InProcessDomainEventDispatcher | 2 |
| Events/Outbox | OutboxInterceptor, InProcessOutboxPublisher, OutboxRelayService | 3 |
| Files | FileService, ImageProcessor, FilesModels, FilesExtensions | 4 |
| Files/Storage | LocalFileStorage | 1 |
| Hosting | DatabaseInitializerService | 1 |
| Json | PostedBodyConvert, HttpRequestExtensions | 2 |
| Menus | MenuServiceCrud, MenuServiceQuery, MenuServiceStatic, MenuDtos | 4 |
| Organizations | OrgTreeService | 1 |
| Permissions | PermissionCatalog, PermissionEvaluator, RequirePermissionAttribute, DistributedPermissionCache | 4 |
| Permissions/Behaviors | PermissionBehavior, PermissionBdd | 2 |
| Permissions/Management | PermissionGrantService | 1 |
| Permissions/Storage | EfPermissionStore | 1 |
| Queries | DynamicQueryExtensions, PagedQuery | 2 |
| Sequences | SequenceFormat, EfSequenceGenerator | 2 |
| ModelBuilders | 统一 ModelBuilder 扩展测试 (6 个扩展方法) | 1 |

### 已知覆盖缺口（跳过，非高质量测试目标）

- `DependencyInjection/` — 纯 IServiceCollection 注册样板代码
- `Files/Storage/AwsS3Storage` + `AliyunOssStorage` — 外部 SDK 依赖，需集成测试
- `DynamicFilters/DynamicFilterProvider` — 需真实 DB 连接和 DbProviderFactory
- `TenE0*` 实体模型类 — 无业务逻辑的 POCO 属性定义
