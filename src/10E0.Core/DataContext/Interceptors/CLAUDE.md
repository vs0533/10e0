# DataContext/Interceptors/ — EF Core 拦截器

## 文件说明

| 文件 | 职责 |
|------|------|
| `AuditInterceptor.cs` | `SaveChangesInterceptor`：自动填充审计字段 + 软删除转换 |

## AuditInterceptor 行为

### 对 `ITimerEntity` 实体：

| 操作 | 自动填充 |
|------|----------|
| `Added` | `CreateTime = now`, `CreateBy = currentUser`, 清空 `UpdateTime`/`UpdateBy` |
| `Modified` | `UpdateTime = now`, `UpdateBy = currentUser`（不动 Create 字段） |

### 对 `ISoftDeleteEntity` 实体：

| 操作 | 转换 |
|------|------|
| `Deleted` | 转为 `Modified`：`IsSoftDelete = true`, `DeleteTime = now`, `DeleteBy = currentUser` |

### 设计决策

- **对比旧 `BaseDataContext` 手动拦截**：旧版在 `SaveChanges` 中手动遍历 `ChangeTracker`，新版用 EF Core 原生 `SaveChangesInterceptor`
- **对比旧 `EFSave.AutomationSystemTakeOverProperties`**：旧版在每个 EntityServer 中手动调用，新版全局拦截，零遗漏
- 当前用户编码通过 `ICurrentUserContext` 获取，不依赖 `E0Context`
