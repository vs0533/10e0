# Auth/Jwt/Storage/ — 认证相关实体与 EF 映射

## 文件说明

| 文件 | 职责 |
|------|------|
| `TenE0User.cs` | 用户基类：`UserCode`、`DisplayName`、`PasswordHash`、`IsActive`、`UserType`、审计字段。业务用户通过继承扩展（如 `AppUser : TenE0User`） |
| `TenE0RefreshToken.cs` | 持久化 Refresh Token：TokenHash (SHA256)、关联用户、过期时间、客户端 IP |
| `TenE0UserRole.cs` | 用户-角色多对多关联表 |
| `AuthModelBuilderExtensions.cs` | EF Core 表映射配置：TPH 继承策略（`TenE0User` 为基表，子类用 Discriminator 区分） |

## 设计决策

- **TPH (Table-Per-Hierarchy)**：`AppUser : TenE0User` 与基类共享同一张表，通过 Discriminator 列区分
- `TenE0User` 继承 `AuditedEntity`（软删除 + 审计字段）
- 密码哈希存 `PasswordHash` 字段，不存明文
