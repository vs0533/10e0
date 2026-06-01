# Permissions/ — 权限系统

基于 Permission Key + 分布式缓存的 RBAC 权限评估。

## 文件说明

| 文件 | 职责 |
|------|------|
| `IPermissionEvaluator.cs` | 权限评估接口：`HasAsync(key)`、`HasAnyAsync(keys)`、`HasAllAsync(keys)` |
| `PermissionEvaluator.cs` | 默认实现：先检查超管角色（短路），再合并所有角色的权限缓存取并集 |
| `IPermissionStore.cs` | 权限存储接口：`GetGrantedPermissionsAsync(roleCodes)` |
| `Permission.cs` / `PermissionDefinition.cs` | 权限元数据：key（如 `"user.update.salary"`）、显示名、分类 |
| `PermissionCatalog.cs` | 权限目录注册表：启动时扫描所有 `IPermissionProvider` 实现，构建完整权限列表 |
| `DistributedPermissionCache.cs` / `IPermissionCache.cs` | 按角色的分布式缓存：每个角色独立缓存其权限集合，支持版本戳全局失效 |
| `RequirePermissionAttribute.cs` | 声明式权限属性：标记在 Command 上，由 `PermissionBehavior` 管道自动检查 |

## 权限检查流

```
HasAsync("user.update.salary")
    → SuperUserRoles 检查（命中直接返回 true）
    → 取当前用户所有角色
    → 对每个角色查 DistributedCache
        → 缓存命中：返回该角色的权限集合
        → 缓存未命中：查 DB（EfPermissionStore）→ 写入缓存
    → 合并所有角色的权限集合（并集）
    → 检查目标 key 是否在并集中
```

## 对比旧 E0Privilege

| 旧版 | 新版 |
|------|------|
| `ControllTag = "Entity\|Field"` 格式 | 自由格式 Permission Key（如 `"user.update.salary"`） |
| `AccessCode` 枚举（None/Read/Create/Update/Delete/All） | 字符串 key，更灵活 |
| `EntityPrivilege` 运行时查 DB | `DistributedPermissionCache` 按角色缓存 |
| 实体级 + 字段级的三层决策逻辑 | 扁平 key 查询，无层级继承 |

## 子目录

| 目录 | 职责 |
|------|------|
| `Behaviors/` | CQRS 管道权限检查行为 |
| `DataFilter/` | 行级数据过滤贡献者接口 |
| `Management/` | 权限授予/撤销管理服务 |
| `Storage/` | 角色实体 + 权限存储实现 + EF 映射 |
