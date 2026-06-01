# Permissions/DataFilter/ — 行级数据过滤

## 文件说明

| 文件 | 职责 |
|------|------|
| `IEntityFilterContributor.cs` | 行级过滤贡献者接口 + 泛型基类 `EntityFilterContributor<T>` |

## 工作原理

```csharp
// 定义一个过滤：DemoEntity 只能看到当前用户所属组织的数据
public class DemoOrgScopedFilter : EntityFilterContributor<DemoEntity>
{
    protected override Expression<Func<DemoEntity, bool>>? Build(BaseDataContext context)
    {
        if (!context.IsAuthenticated) return null; // 未登录不过滤
        if (context.BypassFilters) return null;     // 超管不过滤
        return e => e.OrgIds.Any(id => context.CurrentOrgIds.Contains(id));
    }
}
```

1. `BaseDataContext.OnModelCreating` 扫描所有 `IEntityFilterContributor`
2. 为匹配的实体类型注册 Named Query Filter（名称 `DataPrivilege:Xxx`）
3. EF Core 查询时自动附加过滤条件

## 与 DynamicFilters 的区别

- **`IEntityFilterContributor`**：代码定义的**静态**过滤，编译时确定
- **`DynamicFilters`**：运行时**动态**配置的过滤规则

两者在查询时 AND 组合。

## 注意事项

- 每个 Filter 表达式必须检查 `context.BypassFilters`，否则超管也会被过滤
- Filter 内引用的 `context.CurrentUserCode` 等属性在表达式编译时绑定，运行时通过 DbContext 实例读取
