# 快速入门

5 分钟搭好第一个 10E0 应用。

---

## 1. 前置条件

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- IDE：VS Code（推荐 C# Dev Kit）、Rider、Visual Studio 2022+

## 2. 创建新项目

```bash
dotnet new web -n MyApp
cd MyApp
```

## 3. 安装 NuGet 包

```bash
dotnet add package TenE0.Core
```

## 4. 定义实体

添加 `Product.cs`，继承 `AuditedEntity`（带软删除和时间戳审计字段）：

```csharp
using TenE0.Core.Entities;

public class Product : AuditedEntity
{
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}
```

> 需要领域事件时继承 `AggregateRoot`（位于 `TenE0.Core.Events`）。

## 5. 定义 DbContext

添加 `AppDbContext.cs`，继承 `TenE0SystemDbContext`，框架表自动接入：

```csharp
using TenE0.Core.Abstractions;
using TenE0.Core.DataContext;
using TenE0.Core.DynamicFilters;
using Microsoft.EntityFrameworkCore;

public class AppDbContext(
    DbContextOptions<AppDbContext> options,
    ICurrentUserContext currentUser,
    IDataAccessPolicy accessPolicy,
    IEnumerable<IEntityFilterContributor> filters,
    IDynamicFilterProvider dynamicFilterProvider)
    : TenE0SystemDbContext(options, currentUser, accessPolicy, filters, dynamicFilterProvider)
{
    public DbSet<Product> Products => Set<Product>();
}
```

## 6. 编写 Program.cs

`Program.cs` 中完成全部注册：

```csharp
using TenE0.Core.DependencyInjection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 10E0 核心服务
builder.Services.AddTenE0Core();

// DbContext（此处用 InMemory 数据库演示，开发环境用 UseSqlite/UseSqlServer）
builder.Services.AddTenE0DataContext<AppDbContext>((_, opt) =>
    opt.UseInMemoryDatabase("myapp"));

// CQRS 处理器自动注册
builder.Services.AddTenE0Cqrs(typeof(Program).Assembly);

// 通用 CRUD 服务（写 Command/Handler 时需要）
builder.Services.AddTenE0EntityService();

// 一键启用 JWT 认证 + 权限 + 组织
// 注：单泛型参数使用框架默认 TenE0User/TenE0Role；扩展用户用 AddTenE0Identity<TUser, TContext>
builder.Services.AddTenE0Identity<AppDbContext>(opt =>
{
    // ⚠️ 生产环境必须从配置/环境变量读取，切勿硬编码到源码
    opt.Jwt.SigningKey = builder.Configuration["Jwt:SigningKey"]
        ?? throw new InvalidOperationException("Jwt:SigningKey 未配置");
    opt.Jwt.Issuer = "MyApp";
    opt.Jwt.Audience = "MyApp";
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => new { name = "MyApp", status = "ok" });

app.Run();
```

## 7. 运行

```bash
dotnet run
```

访问 http://localhost:5000，看到响应：

```json
{ "name": "MyApp", "status": "ok" }
```

---

## 接下来

- [认证与 JWT](08-auth-jwt.md) — 登录、注册、令牌刷新
- [CQRS 命令查询](04-cqrs.md) — 命令、查询、处理器、管道行为
- [EntityService CRUD](06-entity-service.md) — 增删改查、部分更新、字段权限

> 使用 `AddTenE0Identity` 默认 Seed 的管理员账号：**admin / 111111**。**首次登录后请立即修改默认密码！**
