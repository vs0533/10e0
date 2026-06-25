# ApiVersioning API 版本化模块（#163）

基于 [Asp.Versioning](https://github.com/dotnet/aspnet-api-versioning)（社区标准，原 Microsoft.AspNetCore.Mvc.Versioning 迁移而来），
在 .NET 10 内置 OpenAPI 基础上提供多版本共存与每版本独立文档。

## 设计原则

**版本透明**：默认版本 `1.0`，未声明版本的请求按默认版本处理（`AssumeDefaultVersionWhenUnspecified=true`），
保证既有裸路由端点（如 `/demo`）引入版本化后行为零变化（向后兼容）。

三种版本声明方式并存（任选其一）：
- **Query string**：`?api-version=1.0`（裸路由下推荐，无需改路由模板）
- **Header**：`X-Api-Version: 1.0`
- **URL segment**：`/v1/demo`（需路由模板含 `{version:apiVersion}` 占位符）

> ⚠️ URL segment 模式仅对路由模板含 `{version:apiVersion}` 的端点生效。裸路由 `/demo` 不支持 URL segment
> （返回 404）——这是路由匹配机制决定的，非 bug。业务方若需 URL segment 版本，把路由改为 `/v{version:apiVersion}/demo`。
> 本期 Demo 端点保持裸路由 + query/header 声明，向后兼容优先。

## 关键文件

- `ApiVersioningOptions.cs` — 框架配置：默认版本号、版本透明开关、ReportApiVersions 开关。
- `DependencyInjection/ApiVersioningExtensions.cs` — `AddTenE0ApiVersioning`（注册版本化 + API Explorer + 版本感知 OpenAPI）
  + `MapTenE0OpenApi`（端点映射，包装 `MapOpenApi().WithDocumentPerVersion()`）。

## 架构

```
AddTenE0ApiVersioning()
    │
    ├─ AddOptions<ApiVersioningOptions>          ← 框架配置（默认 1.0、透明、report）
    │
    ├─ AddApiVersioning().AddApiExplorer()       ← Asp.Versioning 核心 + API Explorer（驱动 OpenAPI 分组）
    │      .AddOpenApi()                          ← 版本感知 OpenAPI transformer
    │
    └─ AddOptions<Asp.Versioning.ApiVersioningOptions>  ← 桥接：框架配置 → Asp.Versioning 内部选项
           .Configure<IServiceProvider>(...)
                 ├─ DefaultApiVersion = (major, minor)
                 ├─ AssumeDefaultVersionWhenUnspecified
                 ├─ ReportApiVersions
                 └─ ApiVersionReader = Combine(UrlSegment, QueryString, Header)

端点侧：
    app.NewApiVersionSet().HasApiVersion(1.0).ReportApiVersions().Build()
    endpoint.WithApiVersionSet(set).HasApiVersion(1.0)
```

### 配置桥接说明

`Asp.Versioning` 自带同名 `ApiVersioningOptions`（框架类与之命名冲突，DI 扩展里用 `TenE0ApiVersioningOptions` 别名消歧）。
框架通过 `AddOptions<Asp.Versioning.ApiVersioningOptions>().Configure<IServiceProvider>(...)` 把自身配置桥接过去——
用带 `IServiceProvider` 的 `Configure` 重载（解析时执行）而非 `AddApiVersioning(Action<>)`，避免回调里解析 sp 触发 root provider 过早构建。

## 用法

### Api 接入（Program.cs）
```csharp
// 服务注册（替代裸 AddOpenApi()，含版本化 + 版本感知 OpenAPI）
builder.Services.AddTenE0ApiVersioning();

// 端点映射（仅 Development）
if (app.Environment.IsDevelopment())
{
    app.MapTenE0OpenApi();      // /openapi/v1.json, /openapi/v2.json ...
    app.MapScalarApiReference(); // Scalar UI 按版本切换
}
```

### 端点声明版本（Minimal API）
```csharp
// 版本集合（一个模块共享）
var versions = app.NewApiVersionSet()
    .HasApiVersion(new ApiVersion(1, 0))
    .ReportApiVersions()
    .Build();

app.MapGet("/demo", ...).WithApiVersionSet(versions).HasApiVersion(new ApiVersion(1, 0));

// 新版本共存（同一端点不同版本，配合 URL segment 路由）
app.MapGet("/v{version:apiVersion}/demo", ...).WithApiVersionSet(versions).HasApiVersion(new ApiVersion(1, 0));
app.MapGet("/v{version:apiVersion}/demo", ...).WithApiVersionSet(versions).HasApiVersion(new ApiVersion(2, 0));
```

### 客户端调用
```bash
# query string（裸路由推荐）
curl /demo?api-version=1.0

# header
curl -H "X-Api-Version: 1.0" /demo

# url segment（需端点路由含 {version:apiVersion}）
curl /v1/demo
```

响应头 `api-supported-versions: 1.0` 告知客户端支持的全部版本。

## 路由策略与端点范围

- **本期范围**：仅 Demo 端点（`DemoEndpoints.cs` 全部端点）加版本化，作为示范。其余端点（Auth/Admin/File/Workflow/Health）未版本化。
- **重要**：Asp.Versioning 开启后，同一模块内若版本化端点与未版本化端点混合，未版本化端点可能因路由策略冲突返回 405。
  故 Demo 模块**全部**端点统一声明 v1.0（含 whoami / 导入导出 / 模板 / 动态查询 / posted-props / partial），
  避免半版本化。业务方按模块整体版本化即可。

## 依赖

| 包 | 版本 | 说明 |
|---|---|---|
| `Asp.Versioning.Http` | 10.0.0 (GA) | Minimal API 版本化核心 |
| `Asp.Versioning.Mvc.ApiExplorer` | 10.0.0 (GA) | API Explorer（驱动 OpenAPI 多版本，Minimal API 也需要） |
| `Asp.Versioning.OpenApi` | 10.0.0-rc.1 | 版本感知 OpenAPI 文档生成 |

> `Asp.Versioning.OpenApi` 截至 .NET 10 RTM 仍为 rc.1，受上游 [dotnet/aspnetcore#66408](https://github.com/dotnet/aspnetcore/issues/66408)
> （AspNetCore.OpenAPI 内部类型不可扩展）阻塞。功能完整可用，待上游 GA 后升级。
> 该包带入 `Microsoft.AspNetCore.OpenApi >= 10.0.6`，Api 项目已同步升级引用。

## 测试

- `tests/10E0.Core.Tests/ApiVersioning/ApiVersioningExtensionsTests.cs` — 8 个单元测试：注册正确性、默认版本 1.0、
  版本透明、ReportApiVersions、复合 reader、自定义配置、配置绑定。
- `tests/10E0.Api.Tests/ApiVersioning/ApiVersioningAcceptanceTests.cs` — 6 个 BDD 集成测试：裸路由无版本返回 200、
  query/header 声明返回 200、URL segment 返回 404（裸路由无占位符）、不支持版本返回 400、响应头含 api-supported-versions。
