# Permissions/Management/ — 权限授予管理

管理员 API：向角色授予/撤销权限。

## 文件说明

| 文件 | 职责 |
|------|------|
| `IPermissionGrantService.cs` | 权限授予服务接口 |
| `PermissionGrantService.cs` | 实现：`GrantAsync(roleCode, permissionKey)`、`RevokeAsync(roleCode, permissionKey)`、`GetRolePermissionsAsync(roleCode)` |

## 安全机制

- `EnsureKeyDefined()`：授予权限前检查 key 是否在 `PermissionCatalog` 中已注册。未注册的 key 会被拒绝，防止拼写错误导致静默授权失败
- 授予/撤销操作会触发 `IPermissionCache` 的角色级缓存失效
