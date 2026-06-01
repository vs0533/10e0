# src/ — 解决方案根目录

包含两个项目：

| 项目 | 类型 | 说明 |
|------|------|------|
| `10E0.Api` | ASP.NET Core (Minimal API) | 应用入口，演示框架全部功能 |
| `10E0.Core` | Class Library | 框架核心，被 Api 引用 |

## 构建

```bash
dotnet build          # 构建整个解决方案
dotnet test           # 运行测试（目前无测试项目）
```

## 项目引用关系

```
10E0.Api → 10E0.Core
```
