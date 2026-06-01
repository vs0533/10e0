# Common/ — 通用工具

## 文件说明

| 文件 | 职责 |
|------|------|
| `ApiResult.cs` | 标准 API 响应封装 `ApiResult<T>`：`Success`/`Error`/`FromErrs(IErrs)` 工厂方法 |

## 用法

```csharp
// 成功
return ApiResult<DemoDto>.Success(data);

// 从 IErrs 转换
if (!errs.IsValid) return ApiResult<T>.FromErrs(errs);

// 错误
return ApiResult<T>.Error("message");
```
