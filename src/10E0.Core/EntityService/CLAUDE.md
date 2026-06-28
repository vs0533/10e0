# EntityService/ — 通用 CRUD 服务

高级实体 CRUD 服务，统一处理部分更新、唯一性校验、字段权限、M:N 关系。

## 文件说明

| 文件 | 职责 |
|------|------|
| `IEntityService.cs` | 接口：`CreateAsync<T>`, `UpdateAsync<T>`, `DeleteAsync<T>` |
| `EntityService.cs` | 实现：完整的 CRUD 流水线 |
| `EntityWriteOptions.cs` | 写操作选项：PostedProperties（局部更新）、PostedNavigations（M:N opt-in）、UniqueValidators、FieldPermissions、BeforeSaveAsync |
| `IEntityQueryService.cs` | 读侧接口：`GetByIdAsync` / `ListAsync` / `PagedAsync` / `CountAsync` / `ExistsAsync`（含投影重载） |
| `EntityQueryService.cs` | 读侧实现：Expression 树 Where + 字段白名单 + BypassFilters + 分页 |
| `EntityReadOptions.cs` | 读操作选项：`EntityReadOptions` + `ReadFilter` + `ReadOperator` + `ReadOrderBy` |

## CRUD 流水线

### Create

```
清理导航属性 → 字段权限检查 → 序列号填充 → 唯一性校验 → Add → BeforeSave → SaveChanges
```

### Update

```
从 DB 加载（含 M:N）→ 唯一性校验(ignoreSelf) → 字段权限检查 → Patch 标量属性 → Diff M:N → BeforeSave → SaveChanges
```

### Delete

```
存在性检查 → Remove → SaveChanges（AuditInterceptor 自动转软删除）
```

## 关键设计

### 部分更新 (PostedProperties)

```csharp
options.PostedProperties = new HashSet<string> { "Name", "Email" }; // 只更新这两个字段
```

审计字段（CreateTime/UpdateBy 等）**硬锁定**，即使在 PostedProperties 中也会被忽略。

### M:N 关系 opt-in (PostedNavigations)

```csharp
options.PostedNavigations = new HashSet<string> { "Tags" }; // 只处理 Tags 这个 M:N
```

**为什么 opt-in**：实体类常把集合属性初始化为空列表，默认全处理会把"未传"误判为"清空"，丢失关联数据。这是旧 E0 的已知隐患。

### 字段权限 (FieldPermissions)

```csharp
options.FieldPermissions = new Dictionary<string, string> { ["Salary"] = "user.update.salary" };
```

写操作时检查：若字段被修改且当前用户无对应权限，则报错。

## 对比旧 BaseEntityServer

| 旧版 | 新版 |
|------|------|
| `BaseCMD.AuthCUD` 控制权限 | `EntityWriteOptions.FieldPermissions` 声明式 |
| `BaseCMD.PostedProp` 散落字段 | `EntityWriteOptions.PostedProperties` 统一收敛 |
| `BaseCMD.OpenInner` 导航控制 | `EntityWriteOptions.KeepNavigationProperties` |
| `EFSave` 图保存 + TrackGraph | 分步：EntityService 处理标量 + RelationProcessor 处理 M:N |
| `RelationProcessor` 依赖 `MultipleEntity` 标记 | 读 EF Core `IModel` Skip Navigation 元数据 |

## 子目录

| 目录 | 职责 |
|------|------|
| `Relations/` | M:N 关系处理器 |
| `Validators/` | 唯一性校验器 |
