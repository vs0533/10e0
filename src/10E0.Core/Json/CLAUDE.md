# Json/ — JSON 序列化工具

## 文件说明

| 文件 | 职责 |
|------|------|
| `HttpRequestExtensions.cs` | `GetPostedPropertiesAsync()`：从 HTTP 请求体解析客户端实际提交的 JSON 属性名集合。支持请求体缓冲（`CanSeek` 检查 + `MemoryStream` 复制） |
| `PostedBodyConvert.cs` | 自定义 `JsonConverter`：配合 PostedProperties 机制，只反序列化客户端提交的字段 |

## PostedProperties 机制

```csharp
// 客户端提交: { "name": "test", "email": "a@b.com" }
// 即使实体有 20 个字段，PostedProperties = ["name", "email"]
var props = await request.GetPostedPropertiesAsync(ct);
// → EntityService.UpdateAsync 只更新 name 和 email
```

## 对比旧版

- 旧版通过 `E0MiddleWare` 中间件缓冲请求体 + `E0ActionFilter` 解析
- 新版内联为扩展方法，不依赖 MVC 管道，Minimal API 友好
- `PostedBodyConvert` 与旧版 `PostedBodyConvert` 功能对等
