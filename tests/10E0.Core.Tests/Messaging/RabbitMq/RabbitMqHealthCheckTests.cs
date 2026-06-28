using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using TenE0.Core.Messaging.RabbitMq;

namespace TenE0.Core.Tests.Messaging.RabbitMq;

/// <summary>
/// #165 RabbitMqHealthCheck 单测。
/// IsConnected=false → Unhealthy；通道探测成功 → Healthy；探测抛异常 → Unhealthy。
/// </summary>
[Trait("Category", "Unit")]
public sealed class RabbitMqHealthCheckTests
{
    private static readonly HealthCheckContext EmptyContext = new();

    private static RabbitMqHealthCheck CreateCheck(
        Mock<IRabbitMqConnectionManager> connMgr,
        RabbitMqOptions? options = null)
        => new(connMgr.Object,
            Options.Create(options ?? new RabbitMqOptions()),
            NullLogger<RabbitMqHealthCheck>.Instance);

    private static Mock<IRabbitMqConnectionManager> ConnectedManager(Mock<IChannel>? channelMock = null)
    {
        channelMock ??= new Mock<IChannel>();
        // ExchangeDeclarePassiveAsync 默认返回成功（loose mock），由具体测试按需覆盖为抛异常。

        var connMgrMock = new Mock<IRabbitMqConnectionManager>();
        connMgrMock.SetupGet(m => m.IsConnected).Returns(true);
        connMgrMock
            .Setup(m => m.GetChannelAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RabbitMqChannelLease(channelMock.Object, () => ValueTask.CompletedTask));
        return connMgrMock;
    }

    [Fact]
    public async Task CheckHealthAsync_NotConnected_ReturnsUnhealthy()
    {
        var connMgr = new Mock<IRabbitMqConnectionManager>();
        connMgr.SetupGet(m => m.IsConnected).Returns(false);

        var check = CreateCheck(connMgr);

        var result = await check.CheckHealthAsync(EmptyContext);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("连接不可用");
        // 未连不应继续探测 channel。
        connMgr.Verify(m => m.GetChannelAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckHealthAsync_ConnectedAndProbeOk_ReturnsHealthy()
    {
        var connMgr = ConnectedManager();

        var check = CreateCheck(connMgr);

        var result = await check.CheckHealthAsync(EmptyContext);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealthAsync_ProbeThrows_ReturnsUnhealthy()
    {
        var channelMock = new Mock<IChannel>();
        channelMock
            .Setup(c => c.ExchangeDeclarePassiveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("broker 拒绝"));
        var connMgr = ConnectedManager(channelMock);

        var check = CreateCheck(connMgr);

        var result = await check.CheckHealthAsync(EmptyContext);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().BeOfType<InvalidOperationException>();
    }
}
