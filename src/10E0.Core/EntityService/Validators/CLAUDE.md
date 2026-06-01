# EntityService/Validators/ — 唯一性校验器

## 文件说明

| 文件 | 职责 |
|------|------|
| `IUniqueValidator.cs` | 唯一性校验器接口 |
| `Unique.cs` | 工厂方法：`Unique.Field<T>(entity, x => x.UserCode)` 和 `Unique.Group<T>(entity, x => x.OrgId, x => x.Code)` |
| `UniqueValidators.cs` | 两种实现：`FieldUniqueValidator`（单字段）和 `GroupUniqueValidator`（组合字段） |

## 用法

```csharp
options.UniqueValidators = [
    Unique.Field<Account>(account, x => x.UserCode),                    // UserCode 全局唯一
    Unique.Group<Account>(account, x => x.OrgId, x => x.Code)           // (OrgId, Code) 组合唯一
];
```

## 行为

- **Create**：检查整个表中是否存在重复
- **Update**：`ignoreSelfId = true`，排除自身记录
- 校验失败时将错误写入 `IErrs`，阻止保存

## 对比旧版

- 旧版 `SimpleUnique` / `GroupUnique` 使用原始 SQL `SELECT COUNT(*)` 查询
- 新版通过 EF Core LINQ 查询，跨数据库兼容
