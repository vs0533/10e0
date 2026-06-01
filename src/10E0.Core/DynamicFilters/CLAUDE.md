# DynamicFilters/ — 动态数据过滤规则引擎

基于 SQL-like 规则的运行时数据过滤系统。管理员可配置过滤规则，运行时自动应用到查询。

## 文件说明

| 文件 | 职责 |
|------|------|
| `ConditionRule.cs` | 过滤条件定义（字段名、运算符、值、逻辑组合） |
| `IDataFilterRuleService.cs` / `DataFilterRuleService.cs` | 规则 CRUD 服务 |
| `IDynamicFilterProvider.cs` / `DynamicFilterProvider.cs` | 从数据库加载规则并在运行时应用 |
| `FilterExpressionBuilder.cs` | 将规则树构建为 LINQ Expression（供 EF Core 翻译为 SQL） |

## 工作原理

1. 管理员通过 `IDataFilterRuleService` 创建/修改过滤规则（存储在 `TenE0DataFilterRule` 表）
2. 查询时 `IDynamicFilterProvider` 加载匹配当前实体的规则
3. `FilterExpressionBuilder` 将规则编译为 `Expression<Func<T, bool>>`
4. 表达式被应用到 IQueryable，EF Core 翻译为 SQL WHERE 子句

## 与行级过滤的关系

- **`IEntityFilterContributor`**（`Permissions/DataFilter/`）：代码定义的静态过滤规则（如"只看本组织数据"），编译时确定
- **`DynamicFilters`**（本目录）：运行时配置的动态过滤规则，管理员可通过 UI 修改

两者在查询时 AND 组合。

## 子目录

| 目录 | 职责 |
|------|------|
| `Storage/` | 规则持久化实体 + EF 映射 |
