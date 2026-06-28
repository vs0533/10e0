# src/ — 源码目录

包含四个项目：

| 项目 | 类型 | 说明 |
|------|------|------|
| `10E0.Api` | ASP.NET Core (Minimal API) | 应用入口，演示框架全部功能 |
| `10E0.Core` | Class Library | 框架核心，被 Api 引用，NuGet 包 `TenE0.Core` |
| `10E0.Core.RabbitMq` | Class Library | 可选 RabbitMQ Outbox Publisher，独立 NuGet 包 `TenE0.Core.RabbitMq`（#165） |
| `10E0.Core.Kafka` | Class Library | 可选 Kafka Outbox Publisher，独立 NuGet 包 `TenE0.Core.Kafka`（#165） |

## 构建

```bash
cd /Users/wilder/dev/10e0
dotnet build 10e0.slnx      # 构建整个解决方案
dotnet test 10e0.slnx       # 运行测试
```

## 项目引用关系

```
10E0.Api → 10E0.Core
10E0.Core.RabbitMq → 10E0.Core   （可选 MQ Publisher，按需引用）
10E0.Core.Kafka → 10E0.Core      （可选 MQ Publisher，按需引用）
```

## NuGet 打包

所有 `IsPackable=true` 的项目（Core / Core.RabbitMq / Core.Kafka）由 CI 发版时遍历 `dotnet pack`，注入同一版本号：

```bash
dotnet pack src/10E0.Core/10E0.Core.csproj -c Release /p:Version=0.0.1
dotnet pack src/10E0.Core.RabbitMq/10E0.Core.RabbitMq.csproj -c Release /p:Version=0.0.1
dotnet pack src/10E0.Core.Kafka/10E0.Core.Kafka.csproj -c Release /p:Version=0.0.1
```

版本号由 CI workflow 自动计算（SemVer patch+1），所有包共享同一版本，本地开发默认 `0.0.0`。

