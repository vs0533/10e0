# src/ — 源码目录

包含两个项目：

| 项目 | 类型 | 说明 |
|------|------|------|
| `10E0.Api` | ASP.NET Core (Minimal API) | 应用入口，演示框架全部功能 |
| `10E0.Core` | Class Library | 框架核心，被 Api 引用，NuGet 包 `TenE0.Core` |

## 构建

```bash
cd /Users/wilder/dev/10e0
dotnet build 10e0.slnx      # 构建整个解决方案
dotnet test 10e0.slnx       # 运行测试
```

## 项目引用关系

```
10E0.Api → 10E0.Core
```

## NuGet 打包

`10E0.Core` 配置了 `IsPackable=true`，CI 发版时自动 `dotnet pack`：

```bash
dotnet pack src/10E0.Core/10E0.Core.csproj -c Release /p:Version=0.0.1
```

版本号由 CI workflow 自动计算（SemVer patch+1），本地开发默认 `0.0.0`。
