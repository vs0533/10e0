# 13 — 组织架构树管理

## 设计动机

企业级应用几乎都面临同一需求：**按组织层级隔离数据**。财务只看本部门及下属部门的数据，总监看整条线。这就需要一个既支持树形操作，又能与行级安全机制集成的组织架构模块。

旧版（E0.Core）使用 SQL Server `HierarchyId`——绑定数据库、不可移植、查询语法非标准。新版改用**物化路径**（Materialized Path），完全基于 EF Core `LIKE` 查询，跨数据库一致工作。

## TenE0Org — 组织实体

```csharp
public class TenE0Org : TreeAuditedEntity
{
    public required string Code { get; set; }        // 业务编码（跨系统对接）
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string Path { get; set; } = "";           // 物化路径
    public int Level { get; set; }                   // 层级深度（根=0）
    public int Order { get; set; }                   // 同级排序
    public bool IsActive { get; set; } = true;
}
```

继承链：`BaseEntity → TimedEntity → AuditedEntity → TreeAuditedEntity`。自动获得 `Id`（GUID-N）、`CreateTime`/`CreateBy`/`UpdateTime`/`UpdateBy`、`IsSoftDelete` 软删除，以及 `ParentId` 父节点字段。

### 物化路径格式

```
格式：/id1/id2/id3/           ← 首尾各一个 "/"
根：  /a1b2c3/               ← Level=0
子：  /a1b2c3/d4e5f6/        ← Level=1
孙：  /a1b2c3/d4e5f6/g7h8i9/ ← Level=2
```

路径片段使用 GUID 字面量（不含分隔符），避免特殊字符干扰 `LIKE` 查询。每个节点的 `Path` 包含自身 Id。

### EF Core 表映射

```csharp
b.Property(o => o.Path).HasMaxLength(512).IsRequired();
b.HasIndex(o => o.Path);           // 子树查询的 LIKE 索引
b.HasIndex(o => o.ParentId);
b.HasIndex(o => o.Code).IsUnique();
```

## IOrgTreeService — 树操作服务

**为什么业务代码必须走服务，不能直接 `dc.Orgs.Add()`？** 因为 `Path` 和 `Level` 的计算逻辑是递归的——自己算很容易出错。服务封装了所有算术规则。

### AddAsync — 添加节点

```csharp
Task<TenE0Org> AddAsync(
    string code, string name,
    string? parentId = null,    // null = 创建根节点
    string? description = null,
    int order = 0,
    CancellationToken ct = default);
```

内部流程：
1. 如果 `parentId` 非空，查询父节点获取 `parent.Path` 和 `parent.Level`
2. 新节点 `Level = parent.Level + 1`（根节点=0）
3. 先 `SaveChanges` 让 EF Core 生成 Id，再用 `parent.Path + node.Id + "/"` 回填 Path

### MoveAsync — 移动子树

```csharp
Task MoveAsync(string nodeId, string? newParentId, CancellationToken ct = default);
```

安全性检查：
- **自引用检测**：`newParentId == nodeId` → 抛出异常
- **循环检测**：`newParent.Path.StartsWith(node.Path)` → 不能移到自己的后代

移动时一次性加载整棵子树（含自身），在内存中批量更新 `Path` 和 `Level`：

```csharp
foreach (var item in subtree)
{
    item.Path = newPath + item.Path[oldPath.Length..];  // 前缀替换
    item.Level += levelDelta;
}
```

企业级组织的子树规模通常可控，这里选择简单可靠的全量更新策略。

### GetDescendantsAsync — 取后代

`LIKE` 查询：`WHERE Path LIKE '/parent/.../%'`，按 `Level` + `Order` 排序。结果不含自身。

### GetAncestorsAsync — 取祖先

分割路径段：`" /a/b/c/ " → ["a", "b", "c"]`，取前 n-1 段作为 Id 集合，`IN` 查询后按 `Level` 降序（从最近的祖先开始）。

### GetSubtreeIdsAsync — 取子树 Id 集合（含自身）

```csharp
Task<IReadOnlySet<string>> GetSubtreeIdsAsync(string nodeId, CancellationToken ct = default);
```

这是**行级安全的关键集成点**。用法：

```csharp
// 业务代码中按组织隔离数据
var orgIds = await orgTree.GetSubtreeIdsAsync(currentUser.OrgId, ct);
query = query.Where(e => e.OrgId != null && orgIds.Contains(e.OrgId));
```

结合动态过滤规则引擎的 `{loginOrg}` 占位符即可实现**自动按部门层级隔离**：用户属于北京分公司，自动可见北京销售部 + 北京技术部的数据。

## API 端点

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/admin/orgs` | 全部组织（按 Path 排序，平铺列表） |
| POST | `/admin/orgs` | 创建组织节点 |
| GET | `/admin/orgs/{id}/subtree` | 子树（含后代的 Id 集合 + 详情） |
| GET | `/admin/orgs/{id}/ancestors` | 祖先链（根→当前父级） |
| POST | `/admin/orgs/{id}/move` | 移动子树到新父节点 |

### POST /admin/orgs

```json
// Request
{ "code": "BJ-SALES", "name": "北京销售部", "parentId": "a1b2c3", "order": 1 }

// Response
{ "id": "d4e5f6", "path": "/a1b2c3/d4e5f6/", "level": 1 }
```

### POST /admin/orgs/{id}/move

```json
// Request
{ "newParentId": "x1y2z3" }    // null 表示移到根
// Response
{ "ok": true }                  // 参数错误返回 400 + error 信息
```

## DI 注册

```csharp
// Program.cs
builder.Services.AddTenE0Organizations<DemoDbContext>();
```

内部注册 `IOrgTreeService → OrgTreeService<TContext>`，作用域生命周期。
