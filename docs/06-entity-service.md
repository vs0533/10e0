# EntityService — 通用 CRUD

`IEntityService` 提供泛型 Create / Update / Delete 操作，内置部分更新、字段级权限、唯一性校验、M:N 关系 diff、流水号自动填充和审计字段保护。

---

## 1. 概述

一个接口覆盖所有实体的写操作，消除为每个实体重复编写 CRUD 的样板代码。核心功能：

- **部分更新**：客户端只提交要改的字段，其余保持不动
- **字段级权限**：按字段粒度控制谁能改什么
- **唯一性校验**：单字段或组合字段防重
- **M:N 关系 diff**：只处理客户端显式提交的导航属性
- **流水号自动填充**：标记 `[Sequence]` 的字段在创建时自动生成
- **审计字段保护**：`CreateTime`、`UpdateBy`、`IsSoftDelete` 等字段由 AuditInterceptor 独占，客户端无法篡改

## 2. DI 注册

```csharp
builder.Services.AddTenE0EntityService();  // 注册 IEntityService (Scoped)
```

必须在 `AddTenE0Core()` 之后调用。`AddTenE0EntityService` 在 `TenE0.Core.DependencyInjection` 命名空间下。

## 3. CreateAsync — 创建

```csharp
await entitySvc.CreateAsync(dc, entity, new EntityWriteOptions
{
    UniqueValidators = [Unique.Field(entity, x => x.Name)],
    FieldPermissions = new Dictionary<string, string> { ["Salary"] = "demo.field.salary" },
    BeforeSaveAsync = _ =>
    {
        // 保存前钩子：此时流水号已分配，可以拿 Id 做后续处理
        Console.WriteLine($"Created: {entity.Id}");
        return Task.CompletedTask;
    }
}, ct);
```

**流水线**：

```
CleanNavigations → FieldPermission → Sequence 填充 → Unique 验证 → Add → BeforeSave → SaveChanges
```

注意 `KeepNavigationProperties` 默认为 false，创建时会自动清理导航属性防止级联写入。

## 4. UpdateAsync — 更新

```csharp
var options = new EntityWriteOptions
{
    PostedProperties = new HashSet<string> { "Name", "Salary" },  // 部分更新，只改这两个字段
    UniqueValidators = [Unique.Field(entity, x => x.Name)],
    FieldPermissions = fieldMap,
};

await entitySvc.UpdateAsync(dc, entity, options, ct);
```

**流水线**：

```
从 DB 加载（含 M:N）→ Unique 验证(ignoreSelf) → FieldPermission → Patch 标量 → Diff M:N → BeforeSave → SaveChanges
```

更新采用"加载 + 补丁"模式：先把完整实体含 M:N 集合从数据库加载出来，再把客户端提交的字段补丁上去。这避免了 EF `Attach` + `Modified` 的两个陷阱：

1. 客户端未传的字段不会被默认值覆盖
2. M:N diff 时加载的实体不会被 EF 身份解析污染

## 5. DeleteAsync — 删除

```csharp
await entitySvc.DeleteAsync(dc, new DemoEntity { Id = id }, ct);
```

- 先检查实体是否存在，不存在则返回 false
- `AuditInterceptor` 自动将实现了 `ISoftDeleteEntity` 的实体转为软删除
- 返回 `true` 表示成功，`false` 表示目标不存在

## 6. EntityWriteOptions 详解

| 属性 | 类型 | 说明 |
|------|------|------|
| `PostedProperties` | `HashSet<string>?` | 客户端提交的字段白名单。null 表示更新全部标量字段（审计字段除外） |
| `PostedNavigations` | `HashSet<string>?` | M:N 导航属性 opt-in。只处理列出的导航，null 表示不处理任何 M:N |
| `KeepNavigationProperties` | `bool` | 创建时是否保留导航属性（默认 false，清理导航防级联写入） |
| `FieldPermissions` | `IReadOnlyDictionary<string, string>?` | 字段名到权限 key 的映射。无权限的字段在写操作时被跳过 |
| `UniqueValidators` | `IReadOnlyList<IUniqueValidator>?` | 唯一性校验器列表，任意一个失败即阻止保存 |
| `BeforeSaveAsync` | `Func<CancellationToken, Task>?` | SaveChanges 前回调，此时所有校验和补丁已完成 |

## 7. 部分更新模式

```csharp
// 客户端提交 JSON: {"name":"new name"}
// 自动提取客户端实际提交的字段名
var postedProps = await http.Request.GetPostedPropertiesAsync(ct);

var entity = await JsonSerializer.DeserializeAsync<DemoEntity>(http.Request.Body, ...);
var options = new EntityWriteOptions { PostedProperties = postedProps };
await entitySvc.UpdateAsync(dc, entity, options, ct);
```

`GetPostedPropertiesAsync` 解析请求体 JSON 的顶层 key，只提取客户端真实提交的字段。未出现在 `PostedProperties` 中的字段不会被写入。

## 8. 唯一性校验

```csharp
// 单字段唯一：Name 在整个表中不能重复
Unique.Field(entity, x => x.Name)

// 组合字段唯一：(TenantId, Code) 联合不能重复
Unique.Group(entity, x => x.TenantId, x => x.Code)
```

- **Create** 场景：检查全表是否有重复值
- **Update** 场景：自动排除自身记录（`ignoreSelfId = true`），避免把自己判为重复
- 校验失败时错误写入 `IErrs`，`SaveChanges` 不会执行

## 9. 字段级权限

```csharp
var fieldPermissions = new Dictionary<string, string>
{
    [nameof(DemoEntity.Salary)] = "demo.field.salary",
};

// 没有 "demo.field.salary" 权限的用户更新时，Salary 字段会被跳过
```

权限检查按场景区分：

- **Create**：所有在 `FieldPermissions` 中列出的字段都会被检查
- **Update**：只检查 `PostedProperties` 中列出的受控字段，减少不必要的权限查询

## 10. 审计字段保护

以下字段被硬锁定，EntityService 在任何写操作中都不会修改。它们由 `AuditInterceptor` 在 `SaveChanges` 时自动填充：

```
Id, CreateTime, CreateBy, UpdateTime, UpdateBy,
IsSoftDelete, DeleteTime, DeleteBy
```

即使客户端恶意提交这些字段（包括将它们放入 `PostedProperties`），也会被 `PatchScalarProperties` 无条件跳过。

---

## 11. 读侧对称：`IEntityQueryService`

本文档只覆盖**写侧**（Create/Update/Delete）。读侧（分页 / 筛选 / 投影）有对等的服务 [`IEntityQueryService`](28-entity-query-service.md) —— 两者共用 `AddTenE0EntityService` 注册、对称的选项对象（`EntityWriteOptions` ↔ `EntityReadOptions`）、一致的"DbContext 由调用方传入"设计。

读场景请看 [28 — 实体读侧查询服务](28-entity-query-service.md)。
