using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TenE0.Core.DependencyInjection;
using TenE0.Core.Events.Outbox;
using TenE0.Core.Messaging.Kafka;
using TenE0.Core.Messaging.RabbitMq;

namespace TenE0.Core.Tests.DependencyInjection;

/// <summary>
/// #165 MessagingExtensions DI 注册测试。
/// 验证 AddTenE0RabbitMqPublisher / AddTenE0KafkaPublisher：
/// - 把 IOutboxPublisher Replace 为对应 Publisher；
/// - 注册连接/Producer 管理器为 Singleton；
/// - 健康检查带 ready 标签。
/// </summary>
[Trait("Category", "Unit")]
public sealed class MessagingExtensionsTests
{
    // ================================================================
    // RabbitMQ
    // ================================================================

    [Fact]
    public void AddTenE0RabbitMqPublisher_ReplacesIOutboxPublisherWithRabbitMq()
    {
        var services = new ServiceCollection();
        // 模拟 AddTenE0DomainEvents 注册的默认进程内 Publisher（被 Replace 覆盖）。
        services.AddScoped<IOutboxPublisher, InProcessOutboxPublisher>();

        services.AddTenE0RabbitMqPublisher();

        var descriptors = services.Where(d => d.ServiceType == typeof(IOutboxPublisher)).ToList();
        descriptors.Should().HaveCount(1, "Replace 应覆盖默认注册而非追加");
        descriptors[0].ImplementationType.Should().Be<RabbitMqPublisher>();
        descriptors[0].Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddTenE0RabbitMqPublisher_RegistersConnectionManagerAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddTenE0RabbitMqPublisher();

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IRabbitMqConnectionManager));
        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationType.Should().Be<RabbitMqConnectionManager>();
    }

    [Fact]
    public void AddTenE0RabbitMqPublisher_RegistersHealthCheckWithReadyTag()
    {
        var services = new ServiceCollection();
        services.AddTenE0RabbitMqPublisher();

        // HealthCheck 注册项存在性 + ready 标签，通过 HealthCheckServiceOptions 直接断言（无需解析
        // HealthCheckService 本身，后者依赖 logging infrastructure）。
        using var sp = services.BuildServiceProvider();
        var registrations = sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        registrations.Should().Contain(r => r.Name == "rabbitmq");
        var rabbitReg = registrations.First(r => r.Name == "rabbitmq");
        rabbitReg.Tags.Should().Contain(ObservabilityExtensions.ReadyTag);
    }

    [Fact]
    public void AddTenE0RabbitMqPublisher_CallbackConfiguresOptions()
    {
        var services = new ServiceCollection();
        services.AddTenE0RabbitMqPublisher(opt =>
        {
            opt.Connection.HostName = "broker.example.com";
            opt.Exchange.Name = "custom.events";
        });

        using var sp = services.BuildServiceProvider();
        // IOptions<RabbitMqOptions> 可解析（未注册 IOptions 相关 infrastructure 时 Configure 仍生效）。
        var options = sp.GetService<IOptions<RabbitMqOptions>>()?.Value;
        options.Should().NotBeNull();
        options!.Connection.HostName.Should().Be("broker.example.com");
        options.Exchange.Name.Should().Be("custom.events");
    }

    // ================================================================
    // Kafka
    // ================================================================

    [Fact]
    public void AddTenE0KafkaPublisher_ReplacesIOutboxPublisherWithKafka()
    {
        var services = new ServiceCollection();
        services.AddScoped<IOutboxPublisher, InProcessOutboxPublisher>();

        services.AddTenE0KafkaPublisher();

        var descriptors = services.Where(d => d.ServiceType == typeof(IOutboxPublisher)).ToList();
        descriptors.Should().HaveCount(1, "Replace 应覆盖默认注册而非追加");
        descriptors[0].ImplementationType.Should().Be<KafkaPublisher>();
        descriptors[0].Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddTenE0KafkaPublisher_RegistersProducerManagerAndProbeAsSingletons()
    {
        var services = new ServiceCollection();
        services.AddTenE0KafkaPublisher();

        var mgr = services.FirstOrDefault(d => d.ServiceType == typeof(IKafkaProducerManager));
        mgr.Should().NotBeNull();
        mgr!.Lifetime.Should().Be(ServiceLifetime.Singleton);
        mgr.ImplementationType.Should().Be<KafkaProducerManager>();

        var probe = services.FirstOrDefault(d => d.ServiceType == typeof(IKafkaMetadataProbe));
        probe.Should().NotBeNull();
        probe!.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddTenE0KafkaPublisher_RegistersHealthCheckWithReadyTag()
    {
        var services = new ServiceCollection();
        services.AddTenE0KafkaPublisher();

        using var sp = services.BuildServiceProvider();
        var registrations = sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        registrations.Should().Contain(r => r.Name == "kafka");
        var kafkaReg = registrations.First(r => r.Name == "kafka");
        kafkaReg.Tags.Should().Contain(ObservabilityExtensions.ReadyTag);
    }

    [Fact]
    public void AddTenE0KafkaPublisher_CallbackConfiguresOptions()
    {
        var services = new ServiceCollection();
        services.AddTenE0KafkaPublisher(opt =>
        {
            opt.BootstrapServers = "kafka1:9092";
            opt.Topic = "custom.events";
        });

        using var sp = services.BuildServiceProvider();
        var options = sp.GetService<IOptions<KafkaOptions>>()?.Value;
        options.Should().NotBeNull();
        options!.BootstrapServers.Should().Be("kafka1:9092");
        options.Topic.Should().Be("custom.events");
    }
}
