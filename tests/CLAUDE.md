# tests/ — 测试项目

| 项目 | 引用 | 用途 |
|------|------|------|
| `10E0.Core.Tests` | 10E0.Core | Core 框架核心逻辑单元测试 |
| `10E0.Api.Tests` | 10E0.Api | Api 集成测试（WebApplicationFactory） |

## 运行

```bash
cd /Users/wilder/dev/10e0
dotnet test 10e0.slnx                          # 全部测试
dotnet test tests/10E0.Core.Tests              # 仅 Core 测试
dotnet test tests/10E0.Api.Tests               # 仅 Api 测试
dotnet test --collect:"XPlat Code Coverage"    # 带覆盖率
```

## 技术栈

- **xUnit** — 测试框架
- **coverlet** — 代码覆盖率收集
- **Microsoft.EntityFrameworkCore.InMemory** — EF Core 内存数据库（Core 测试）
- **Microsoft.AspNetCore.Mvc.Testing** — WebApplicationFactory（Api 集成测试）

## 当前状态

测试项目为占位状态（`PlaceholderTests.cs`），仅验证程序集可加载。后续按模块逐步添加真实测试。
