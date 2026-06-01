# Entities/ — 实体基类层次

定义所有领域实体的基类继承链。

## 文件说明

| 文件 | 职责 |
|------|------|
| `BaseEntity.cs` | 实体基类层次定义 |

## 继承链

```
BaseEntity                    ← 仅主键 (Id = GUID string)
├── TimedEntity               ← + 审计字段 (CreateTime/UpdateBy 等)
│   └── AuditedEntity         ← + 软删除 (IsSoftDelete/DeleteTime/DeleteBy)  ← 最常用
│       └── TreeAuditedEntity ← + 树形 (ParentId)
```

## 对应标记接口

| 基类 | 实现接口 | 被谁处理 |
|------|----------|----------|
| `BaseEntity` | `IBaseEntity` | EF 映射 |
| `TimedEntity` | `ITimerEntity` | `AuditInterceptor` 自动填充 |
| `AuditedEntity` | `ISoftDeleteEntity` | `AuditInterceptor` 转换 Delete → 标记删除；`BaseDataContext` 注册 SoftDelete 查询过滤 |
| `TreeAuditedEntity` | `ITreeEntity` | 应用层 OrgTreeService / MenuService 处理父子关系 |

## 设计决策

- **对比旧 `BaseEntity`**：旧版主键类型不统一（有 Guid、有 int），新版强制 `string`（GUID 字面量）
- **对比旧 `TreeEntity<T>`**：旧版用泛型自引用（`TreeEntity<Menu>`），新版 `TreeAuditedEntity` 非泛型，`ParentId` 是 `string?`
- **不再需要 `MultipleEntity`**：M:N 关系改用 EF Core Skip Navigation 自省（见 `EntityService/Relations/`）
- **领域事件**：需要领域事件的实体继承 `AggregateRoot`（在 `Events/` 目录），而非 `BaseEntity`
