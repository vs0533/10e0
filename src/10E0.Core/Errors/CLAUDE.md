# Errors/ — 请求级错误收集

## 文件说明

| 文件 | 职责 |
|------|------|
| `Errs.cs` | `IErrs` 默认实现。`Dictionary<string, ErrorEntry>` 存储，支持 `Add(message, key, code)`、`IsValid`、`Clear()` |

## 用法

```csharp
// 注入
public MyHandler(IErrs errs) { _errs = errs; }

// 收集错误
errs.Add("用户名已存在", key: "UserCode", code: "UNIQUE");

// 检查
if (!errs.IsValid) return false;

// API 层转换
return ApiResult<T>.FromErrs(errs);
```

## 设计决策

- **对比旧 `ModelStateProvider`**：旧版包装 ASP.NET `ModelStateDictionary`，与 MVC 耦合。新版独立 DI 服务，Minimal API 友好
- **Scoped 生命周期**：每个请求一个实例，全管道共享
- **注意**：权限失败抛 `PermissionDeniedException`，不走 IErrs。这是混合模式（异常 + 错误袋）
