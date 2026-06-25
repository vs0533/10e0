# 24 — API 版本化（Asp.Versioning）

基于 [Asp.Versioning](https://github.com/dotnet/aspnet-api-versioning)（社区标准）实现 API 多版本共存，配合 .NET 10 内置 OpenAPI 生成每版本独立文档。**版本透明策略**：默认版本 `1.0`，未声明版本的请求按默认处理，既有端点零改动。

所有代码位于 `TenE0.Core.ApiVersioning`，DI / 端点扩展在 `TenE0.Core.DependencyInjection.ApiVersioningExtensions`。

---

## 设计：版本透明

引入版本化最常见的破坏性问题是「老客户端不带版本就 404」。本框架默认 `AssumeDefaultVersionWhenUnspecified = true`，
未声明版本的请求按默认版本 `1.0` 处理 —— 既有裸路由端点（如 `/demo`）引入版本化后行为零变化（向后兼容）。

三种版本声明方式并存（客户端任选其一）：

| 方式 | 示例 | 适用场景 |
|------|------|----------|
| **Query string** | `/demo?api-version=1.0` | 裸路由推荐，无需改路由模板 |
| **Header** | `X-Api-Version: 1.0` | 不污染 URL，适合内部服务间调用 |
| **URL segment** | `/v1/demo` | 需端点路由含 `{version:apiVersion}` 占位符 |

> **URL segment 限制**：仅对路由模板含 `{version:apiVersion}` 的端点生效。裸路由 `/demo` 不支持 URL segment（返回 404）。
> 业务方若需 URL segment 版本，把路由改为 `/v{version:apiVersion}/demo`。本期 Demo 端点保持裸路由 + query/header 声明。

成功响应头返回 `api-supported-versions: 1.0`，客户端可探测升级路径。

---

## 快速开始

```csharp
// Program.cs —— 替代裸 AddOpenApi()，含版本化 + 版本感知 OpenAPI
builder.Services.AddTenE0ApiVersioning();

// ... 中间件管线 ...

// 端点映射（仅 Development）
if (app.Environment.IsDevelopment())
{
    app.MapTenE0OpenApi();       // /openapi/v1.json, /openapi/v2.json ...
    app.MapScalarApiReference(); // Scalar UI 按版本切换
}
```

### 端点声明版本（Minimal API）

```csharp
// DemoEndpoints.cs —— 一个模块共享一个 ApiVersionSet
var versions = app.NewApiVersionSet()
    .HasApiVersion(new ApiVersion(1, 0))
    .ReportApiVersions()        // 响应头返回 api-supported-versions
    .Build();

app.MapGet("/demo", ...)
    .WithApiVersionSet(versions)
    .HasApiVersion(new ApiVersion(1, 0));
```

### 多版本共存（同一端点不同版本）

URL segment 路由模式下，同一端点可声明多个版本：

```csharp
app.MapGet("/v{version:apiVersion}/demo", LegacyDemoHandler)
    .WithApiVersionSet(versions)
    .HasApiVersion(new ApiVersion(1, 0));   // v1：老响应结构

app.MapGet("/v{version:apiVersion}/demo", NewDemoHandler)
    .WithApiVersionSet(versions)
    .HasApiVersion(new ApiVersion(2, 0));   // v2：新响应结构
```

OpenAPI 文档会为每个版本生成独立条目（`/openapi/v1.json`、`/openapi/v2.json`），Scalar UI 提供版本下拉切换。

---

## 客户端调用

```bash
# query string（裸路由推荐）
curl /demo?api-version=1.0

# header
curl -H "X-Api-Version: 1.0" /demo

# url segment（需端点路由含 {version:apiVersion}）
curl /v1/demo
```

请求不支持的版本（如 `/demo?api-version=9.0`）返回 `400 Bad Request`。

---

## 配置

```csharp
builder.Services.AddTenE0ApiVersioning(opt =>
{
    opt.DefaultMajorVersion = 2;           // 默认主版本（默认 1）
    opt.DefaultMinorVersion = 0;           // 默认次版本（默认 0）
    opt.AssumeDefaultVersionWhenUnspecified = false; // 强制客户端显式声明版本
    opt.ReportApiVersions = true;          // 响应头通告支持版本（默认 true）
});
```

---

## 端点范围与注意事项

- **本期范围**：仅 Demo 端点（`DemoEndpoints.cs` 全部端点）加版本化作为示范。其余端点（Auth/Admin/File/Workflow/Health）未版本化。
- **模块整体版本化**：Asp.Versioning 开启后，同一模块内若版本化端点与未版本化端点混合，未版本化端点可能因路由策略冲突返回 405。
  故 Demo 模块**全部**端点统一声明 v1.0。业务方按模块整体版本化即可。
- **非业务端点**：框架自带的基础设施端点（如根 `/`、健康检查）无需版本化 —— 它们不承载业务语义。

---

## 依赖

| 包 | 版本 | 说明 |
|---|---|---|
| `Asp.Versioning.Http` | 10.0.0 (GA) | Minimal API 版本化核心 |
| `Asp.Versioning.Mvc.ApiExplorer` | 10.0.0 (GA) | API Explorer，驱动 OpenAPI 多版本（Minimal API 也需要） |
| `Asp.Versioning.OpenApi` | 10.0.0-rc.1 | 版本感知 OpenAPI 文档生成 |

> `Asp.Versioning.OpenApi` 截至 .NET 10 RTM 仍为 rc.1，受上游 [dotnet/aspnetcore#66408](https://github.com/dotnet/aspnetcore/issues/66408)
> （AspNetCore.OpenAPI 内部类型不可扩展）阻塞。功能完整可用，待上游 GA 后升级。该包带入 `Microsoft.AspNetCore.OpenApi >= 10.0.6`，
> Api 项目已同步升级引用。

---

## 测试

- `tests/10E0.Core.Tests/ApiVersioning/` — 单元测试：注册正确性、默认版本、版本透明、复合 reader、配置桥接。
- `tests/10E0.Api.Tests/ApiVersioning/` — BDD 集成测试：三种版本声明方式、不支持版本 400、响应头 api-supported-versions。
