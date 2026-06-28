# 27. 消息队列集成（RabbitMQ / Kafka Publisher）

框架默认的领域事件投递是进程内的（`InProcessOutboxPublisher`，见 [10. 领域事件](10-domain-events.md)）。但在多实例部署、与外部系统解耦、事件驱动微服务等场景下，需要把事件投递到真正的消息中间件。

10E0 提供了两个**可选的官方 MQ Publisher** 实现，复用现有 `IOutboxPublisher` 抽象，**业务代码零改动**：

| Publisher | 适用场景 | NuGet 包 | 依赖 |
|-----------|---------|---------|------|
| **RabbitMQ** | 中小规模、低延迟、事务性投递、灵活路由 | `TenE0.Core.RabbitMq` | `RabbitMQ.Client` 7.x（~1MB） |
| **Kafka** | 大规模、高吞吐、事件溯源、日志聚合 | `TenE0.Core.Kafka` | `Confluent.Kafka` 2.x（含 librdkafka ~10MB） |

## 27.1 为什么是独立 NuGet 包

MQ 客户端是重依赖（librdkafka 原生库 10MB+）。如果把它们塞进 `TenE0.Core` 主包，所有用户（哪怕不用 MQ）都会被迫引入这些依赖。因此框架把它们拆成独立包，**按需引用**：

```bash
# 只在你需要 MQ 投递的项目里加
dotnet add package TenE0.Core.RabbitMq   # 或 TenE0.Core.Kafka
```

主包 `TenE0.Core` 始终零 MQ 依赖。

## 27.2 三行接入（业务代码零改动）

`IOutboxPublisher` 是 OutboxRelayService 与投递机制之间的唯一接缝。切换投递机制时，聚合、Handler、命令代码**无需任何改动**，只需替换这一个 DI 注册：

```csharp
// Program.cs
builder.Services.AddTenE0DomainEvents<AppDbContext>();        // 注册默认进程内 Publisher
builder.Services.AddTenE0RabbitMqPublisher(opt =>             // Replace 为 RabbitMQ
{
    opt.Connection.HostName = "rabbitmq";
    opt.Exchange.Name = "tene0.domain-events";
});
// 或切 Kafka：
// builder.Services.AddTenE0KafkaPublisher(opt =>
// {
//     opt.BootstrapServers = "kafka1:9092,kafka2:9092";
// });
```

`AddTenE0RabbitMqPublisher` / `AddTenE0KafkaPublisher` 一次性完成：
1. **Replace** `IOutboxPublisher` 为对应 MQ 实现
2. 注册连接/Producer 管理器（Singleton，跨请求复用连接）
3. 注册健康检查（带 `ready` 标签，纳入 `/health/ready` 探针，见 [26. 可观测性](26-observability.md)）

## 27.3 RabbitMQ Publisher

### 投递语义

| 特性 | 实现 |
|------|------|
| **持久化** | `Persistent=true` + 持久化 exchange，防 broker 重启丢消息 |
| **幂等键** | `MessageId = OutboxMessage.Id`，下游消费者据此去重 |
| **路由** | `Type = EventType`（CLR 全名）+ `RoutingKey = EventType`，topic exchange 支持按事件类型模式订阅 |
| **自动重连** | `AutomaticRecoveryEnabled=true`（v7 默认开），broker 断线自动重连 + 拓扑恢复 |
| **可靠性兜底** | 投递失败抛异常 → `OutboxRelayService` 重试 → `MaxAttempts` 兜底，绝不丢消息 |

> **关于 Publisher Confirms**：`RabbitMQ.Client` v7 移除了传统的 `ConfirmSelect`/`WaitForConfirms` API，改为基于 `BasicAcks`/`BasicNacks` 事件的异步模型。本实现不实现该复杂事件协调 —— Outbox 模式本身就是为"至少一次"投递设计的：投递失败消息不会标记 `SentTime`，Relay 会幂等重发。配合下游消费者按 `MessageId` 去重，即可达到 exactly-once 语义。

### 配置项（`RabbitMqOptions`）

```csharp
new RabbitMqOptions
{
    Connection =
    {
        HostName = "localhost",      // broker 主机
        Port = 5672,                 // AMQP 端口
        UserName = "guest",          // 生产应显式覆盖
        Password = "",
        VirtualHost = "/",
        MaxConnections = 5,          // 连接池大小（channel 每次创建-释放，IChannel 不可跨线程并发 publish）
    },
    Exchange =
    {
        Name = "tene0.domain-events",
        Type = "topic",              // 支持按事件类型路由键模式订阅
        Durable = true,              // broker 重启后保留
    },
}
```

也可从 `appsettings.json` 绑定：

```csharp
builder.Services.AddTenE0RabbitMqPublisher(
    builder.Configuration.GetSection("RabbitMq"));
```

```json
{
  "RabbitMq": {
    "Connection": { "HostName": "rabbitmq", "UserName": "app", "Password": "secret" },
    "Exchange": { "Name": "tene0.domain-events", "Type": "topic" }
  }
}
```

### 连接管理

`RabbitMqConnectionManager`（Singleton）的池化策略：

- **池化 Connection，不池化 Channel** —— `IChannel` 禁止跨线程并发 publish，故 channel 每次 create → publish → dispose（轻量），而 Connection（TCP 长连接、自动重连）才值得池化复用。
- **懒初始化** —— 首次 `GetChannelAsync` 时才建连，避免启动期 broker 不可达阻塞宿主启动。
- **死连接回收** —— 借出连接若已断开（`IsOpen=false`）不回池，下次建新连接替代。

## 27.4 Kafka Publisher

### 投递语义

| 特性 | 实现 |
|------|------|
| **幂等键** | `Key = OutboxMessage.Id` —— 同 key 进同 partition，保证单事件有序 + 下游可去重 |
| **路由头** | `Headers{eventType, occurredAt}` —— 单 topic 多事件类型可在消费端按头过滤 |
| **可靠性** | `Acks.All`（全 ISR ACK）+ `EnableIdempotence=true`（broker 侧去重） |
| **投递确认** | 只有 `PersistenceStatus.Persisted` 才算成功落盘，否则抛异常让 Relay 重试 |
| **批量** | `LingerMs=5`（攒 5ms 批量发），提升吞吐 |

### 配置项（`KafkaOptions`）

```csharp
new KafkaOptions
{
    BootstrapServers = "localhost:9092",  // 逗号分隔多 broker
    Topic = "tene0.domain-events",       // 默认单 topic；可用 TopicResolver 按事件类型分 topic
    Acks = Acks.All,                     // 全 ISR ACK
    EnableIdempotence = true,            // broker 侧去重
    LingerMs = 5,                        // 攒批毫秒
    MessageTimeout = TimeSpan.FromSeconds(5),  // 单条投递超时，超时抛异常让 Relay 重试
}
```

**按事件类型分 topic**（可选）：

```csharp
builder.Services.AddTenE0KafkaPublisher(opt =>
{
    opt.BootstrapServers = "kafka:9092";
    opt.TopicResolver = eventType => eventType switch
    {
        "TenE0.Audit.UserLoggedInEvent" => "tene0.audit",
        _ => "tene0.domain-events",     // 默认
    };
});
```

### Producer 管理

`KafkaProducerManager`（Singleton）：`Confluent.Kafka` 的 `IProducer<TKey,TValue>` 本身就是线程安全长连接（底层 librdkafka 自动重连、连接池化），无需自管连接池。管理器仅按配置创建唯一 Producer 实例缓存复用，进程生命周期内不重建。

## 27.5 与 Outbox / 健康检查的协同

### 与 Outbox Relay 的协同

`OutboxRelayService` 调用 `IOutboxPublisher.PublishAsync`（见 [10.6 Outbox Relay](10-domain-events.md)）：

```
PublishAsync 成功 → 标记 OutboxMessage.SentTime（已发送）
PublishAsync 抛异常 → LastError 记录原因，AttemptCount++，下次轮询重试
AttemptCount >= MaxAttempts → 停止重试（毒消息，需 DLQ 工具人工介入）
```

MQ 重连期间，所有 Publisher 调用抛异常 → Relay 重试 → **不会丢消息**（Outbox 行级锁 + MaxAttempts 兜底）。

### 与健康检查的协同（#161）

两个 Publisher 各自注册一个健康检查，带 `ready` 标签纳入 `/health/ready`：

| 检查名 | 探测方式 | 不可用时 |
|--------|---------|---------|
| `rabbitmq` | `IsConnected` + `ExchangeDeclarePassive`（探测目标交换机存在性） | `Unhealthy` → K8s readiness 摘流 |
| `kafka` | 独立 `AdminClient` 请求集群 metadata（brokers > 0） | `Unhealthy` → K8s readiness 摘流 |

这避免把流量打到无法投递事件的实例（Outbox 会积压，积压告警由 `OutboxHealthCheck` 负责）。

## 27.6 关键决策点

| 决策 | 选择 | 理由 |
|------|------|------|
| 进主包 vs 独立包 | **独立 NuGet 包** | MQ 客户端重依赖（11MB+），不污染不用 MQ 的用户 |
| RabbitMQ.Client vs MassTransit | 直接用 `RabbitMQ.Client` | 官方、轻；MassTransit 抽象层太厚，与框架已有 `IOutboxPublisher` 抽象重复 |
| Kafka 客户端 | `Confluent.Kafka` | .NET 生态事实标准，性能最好（基于 librdkafka） |
| 是否引入 CAP | **不引入** | CAP 与框架 Outbox 抽象重复（框架已有持久化 + Relay），只缺最后的 Publisher |
| 消息格式 | JSON | 与 Outbox 现有 `PayloadJson` 一致；Avro/Protobuf 后续可加 |
| 消费端 | **本期不做** | 只做 Publisher（出方向）；订阅 MQ → 触发领域事件作为后续 issue |

## 27.7 本期范围外

- **消费端**：订阅 MQ 消息触发领域事件 / 命令（入方向）。本期只做 Publisher（出方向）。
- **Schema Registry**：Avro / Protobuf 序列化。本期用 JSON。
- **CAP 集成**：见上表，不引入。
- **自定义 DLQ 策略**：超 `MaxAttempts` 的毒消息保留在 Outbox 表，由 `IOutboxAdmin`（见 [10.7 毒消息管理](10-domain-events.md)）查询 / 导出 / 手动重试，本期不做自动 DLQ 投递。
