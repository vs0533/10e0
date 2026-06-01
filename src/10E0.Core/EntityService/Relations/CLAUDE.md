# EntityService/Relations/ — M:N 关系处理器

## 文件说明

| 文件 | 职责 |
|------|------|
| `RelationProcessor.cs` | 处理实体创建和更新时的多对多（Skip Navigation）关系 |

## 核心方法

### Create 场景：`CleanNavigations`

- 清理所有普通导航属性（防止 EF 级联写入客户端提供的对象图）
- 保留 Skip Navigation（M:N），支持带关联关系的创建

### Update 场景：`DiffSkipNavigations`

- 对比 DB 中现有 M:N 关联与客户端提交的关联
- 计算 add/remove 差异集
- 只处理 `PostedNavigations` 中显式列出的导航（opt-in 语义）

## 设计决策

- **不依赖 `MultipleEntity` 标记基类**：直接读 EF Core `IModel` 的 Skip Navigation 元数据
- **不维护 MetaContext 反射缓存**：EF Core 已有完整元数据
- **PK 身份比较**：diff 算法只比较关联对象的 Id，客户端只需传 `{ Id: "xxx" }` 的 stub 引用
- **只处理 M:N**：Many-to-One 关系（外键变更）需由命令处理器手动处理
