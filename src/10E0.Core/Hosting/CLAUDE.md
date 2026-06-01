# Hosting/ — 启动期服务

## 文件说明

| 文件 | 职责 |
|------|------|
| `DatabaseInitializerService.cs` | `IHostedService`：应用启动时执行数据库迁移和种子数据（Seeder） |

## 行为

1. 在 `StartingAsync` 阶段运行（早于请求处理）
2. 执行 `context.Database.Migrate()`（如果配置了自动迁移）
3. 按顺序执行所有注册的 Seeder（`PermissionSeeder` → `AuthSeeder` → `MenuSeeder`）
4. 完成后释放 DbContext

## 注意事项

- Seeder 是幂等的：已存在的数据不会重复插入
- 失败时记录日志但不阻塞启动（可配置为阻塞）
