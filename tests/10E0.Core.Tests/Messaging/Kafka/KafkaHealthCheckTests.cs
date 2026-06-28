using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TenE0.Core.Messaging.Kafka;

namespace TenE0.Core.Tests.Messaging.Kafka;

/// <summary>
/// #165 KafkaHealthCheck 单测。
/// brokers &gt; 0 → Healthy；brokers == 0 → Unhealthy；探测异常 → Unhealthy。
/// </summary>
[Trait("Category", "Unit")]
public sealed class KafkaHealthCheckTests
{
    private static readonly HealthCheckContext EmptyContext = new();

    private static KafkaHealthCheck CreateCheck(Mock<IKafkaMetadataProbe> probe, KafkaOptions? options = null)
        => new(Options.Create(options ?? new KafkaOptions()),
            NullLogger<KafkaHealthCheck>.Instance,
            probe.Object);

    [Fact]
    public async Task CheckHealthAsync_BrokersAvailable_ReturnsHealthy()
    {
        var probe = new Mock<IKafkaMetadataProbe>();
        probe.Setup(p => p.GetBrokerCountAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var check = CreateCheck(probe);

        var result = await check.CheckHealthAsync(EmptyContext);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("brokers=3");
    }

    [Fact]
    public async Task CheckHealthAsync_ZeroBrokers_ReturnsUnhealthy()
    {
        var probe = new Mock<IKafkaMetadataProbe>();
        probe.Setup(p => p.GetBrokerCountAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var check = CreateCheck(probe);

        var result = await check.CheckHealthAsync(EmptyContext);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("0 个 broker");
    }

    [Fact]
    public async Task CheckHealthAsync_ProbeThrows_ReturnsUnhealthyWithException()
    {
        var probe = new Mock<IKafkaMetadataProbe>();
        probe.Setup(p => p.GetBrokerCountAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("broker 不可达"));

        var check = CreateCheck(probe);

        var result = await check.CheckHealthAsync(EmptyContext);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task CheckHealthAsync_PassesConfiguredServersAndTopic()
    {
        var probe = new Mock<IKafkaMetadataProbe>();
        string? capturedServers = null;
        string? capturedTopic = null;
        probe.Setup(p => p.GetBrokerCountAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((servers, topic, _) =>
            {
                capturedServers = servers;
                capturedTopic = topic;
            })
            .ReturnsAsync(1);

        var check = CreateCheck(probe, new KafkaOptions
        {
            BootstrapServers = "kafka1:9092,kafka2:9092",
            Topic = "custom.events",
        });

        await check.CheckHealthAsync(EmptyContext);

        capturedServers.Should().Be("kafka1:9092,kafka2:9092");
        capturedTopic.Should().Be("custom.events");
    }
}
