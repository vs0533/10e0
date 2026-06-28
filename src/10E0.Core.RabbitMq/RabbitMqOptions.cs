namespace TenE0.Core.Messaging.RabbitMq;

/// <summary>
/// RabbitMQ Publisher 配置（issue #165）。
///
/// <para>
/// 与 <c>OutboxRelayOptions</c> 平行的独立选项 —— Relay 只管"拉取批次 → 调投递器 → 重试"，
/// 投递机制细节（连接、交换机、Publisher Confirms）由本选项承载。
/// </para>
/// </summary>
public sealed class RabbitMqOptions
{
    /// <summary>配置节名。应用可从 <c>appsettings.json</c> 的 <c>"RabbitMq"</c> 段绑定。</summary>
    public const string SectionName = "RabbitMq";

    /// <summary>Broker 连接参数。</summary>
    public RabbitMqConnectionOptions Connection { get; set; } = new();

    /// <summary>目标交换机声明（启动时幂等 declare）。</summary>
    public RabbitMqExchangeOptions Exchange { get; set; } = new();
}

/// <summary>Broker 连接参数。</summary>
public sealed class RabbitMqConnectionOptions
{
    /// <summary>主机名。默认 <c>localhost</c>。</summary>
    public string HostName { get; set; } = "localhost";

    /// <summary>AMQP 端口。默认 <c>5672</c>。</summary>
    public int Port { get; set; } = 5672;

    /// <summary>用户名。默认 <c>guest</c>（仅本地，生产应显式覆盖）。</summary>
    public string UserName { get; set; } = "guest";

    /// <summary>密码。</summary>
    public string Password { get; set; } = "";

    /// <summary>虚拟主机。默认 <c>/</c>。</summary>
    public string VirtualHost { get; set; } = "/";

    /// <summary>
    /// 连接池大小（长连接复用，channel 每次创建-释放）。默认 5：
    /// 够覆盖 Outbox Relay 的并发投递（BatchSize 默认 50，但单 channel 串行 publish，
    /// 多连接并行才能撑高吞吐）。生产高吞吐可调大。
    /// </summary>
    public int MaxConnections { get; set; } = 5;

    /// <summary>
    /// 每个连接的 channel 上限（RabbitMQ broker 侧资源）。默认 50。
    /// </summary>
    public int MaxChannelsPerConnection { get; set; } = 50;
}

/// <summary>目标交换机声明参数。</summary>
public sealed class RabbitMqExchangeOptions
{
    /// <summary>交换机名。默认 <c>tene0.domain-events</c>。</summary>
    public string Name { get; set; } = "tene0.domain-events";

    /// <summary>
    /// 交换机类型。默认 <c>topic</c> —— 支持 <c>RoutingKey</c> 模式匹配（按事件类型订阅）。
    /// </summary>
    public string Type { get; set; } = "topic";

    /// <summary>是否持久化（broker 重启后保留）。默认 <c>true</c>。</summary>
    public bool Durable { get; set; } = true;
}
