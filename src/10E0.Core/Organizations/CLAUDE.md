# Organizations/ — 组织架构树管理

组织层级的 CRUD 和树形操作。

## 文件说明

| 文件 | 职责 |
|------|------|
| `IOrgTreeService.cs` | 组织树服务接口 |
| `OrgTreeService.cs` | 完整实现：Add、Move、GetSubtreeIds、GetDescendants、GetAncestors。维护 `Path`（物化路径）和 `Level`（层级深度） |
| `TenE0Org.cs` | 组织实体：Id、Code、Name、ParentId、Path、Level、Description。继承 `AuditedEntity` |
| `OrgModelBuilderExtensions.cs` | EF Core 表映射 |

## 物化路径 vs HierarchyId

新版选择**物化路径**（`Path` 字段存储 `/root/parent/self` 格式），而非旧版的 SQL Server `HierarchyId`。

理由：
- 跨数据库兼容（不绑定 SQL Server）
- 子树查询用 `LIKE '/root/parent/%'` 即可，性能好
- 移动操作只需更新子树的 Path 前缀

## 对比旧版

- 旧版 `E0Org : TreeTimerISoftDeleteEntity<E0Org>` + 可选 HierarchyId
- 新版 `TenE0Org : AuditedEntity` + 物化路径，由 `OrgTreeService` 维护一致性
