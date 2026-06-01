# Queries/ — 动态查询与分页

## 文件说明

| 文件 | 职责 |
|------|------|
| `DynamicQueryExtensions.cs` | 扩展方法：`DynamicWhere`（动态 WHERE）、`DynamicOrderBy`（动态排序）、`Page`（分页）。基于 `System.Linq.Dynamic.Core` |
| `PagedQuery.cs` | 分页请求 DTO `PagedQuery`（CurrentPage、PageSize）+ 分页结果 `PagedResult<T>`（Items、Total、CurrentPage、PageSize、TotalPages） |

## 用法

```csharp
// 动态 WHERE
var query = dbSet.DynamicWhere("Name.Contains(@0) && IsActive == true", "张");

// 动态排序
query = query.DynamicOrderBy("CreateTime desc, Name asc");

// 分页
var result = await query.Page(pagedQuery).ToListAsync();
// → PagedResult<T> { Items, Total, CurrentPage, PageSize, TotalPages }
```

## 对比旧版

- 旧版 `DynamicQueryable` / `DynamicLinq` 是自建实现
- 新版使用成熟的 `System.Linq.Dynamic.Core` 库，功能更完善
