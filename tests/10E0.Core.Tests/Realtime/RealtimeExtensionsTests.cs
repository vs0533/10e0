using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using TenE0.Core.DependencyInjection;
using TenE0.Core.Events;
using TenE0.Core.Realtime;

namespace TenE0.Core.Tests.Realtime;

[Trait("Category", "Unit")]
public sealed class RealtimeExtensionsTests
{
    [Fact]
    public void AddTenE0Realtime_RegistersAllExpectedServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(); // SignalR 的 hub 激活依赖 ILogger<>

        services.AddTenE0Realtime();
        var sp = services.BuildServiceProvider();

        sp.GetService<IRealtimeNotifier>().Should().BeOfType<HubBasedRealtimeNotifier>();
        sp.GetService<IRealtimeGroupProvider>().Should().BeOfType<ClaimBasedGroupProvider>();
        sp.GetService<IUserIdProvider>().Should().BeOfType<ClaimBasedUserIdProvider>();
        sp.GetService<IRealtimeBackplane>().Should().BeOfType<NoopRealtimeBackplane>();
    }

    [Fact]
    public void AddTenE0Realtime_RegistersOpenGenericDispatcher_ForNotifyClientEvents()
    {
        // 声明式触发核心：IDomainEventHandler<OrderApprovedEvent> 能解析出 NotificationDispatcher<OrderApprovedEvent>
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTenE0Realtime();
        var sp = services.BuildServiceProvider();

        var handler = sp.GetService<IDomainEventHandler<TestNotifyEvent>>();

        handler.Should().NotBeNull().And.BeOfType<NotificationDispatcher<TestNotifyEvent>>();
    }

    [Fact]
    public void AddTenE0Realtime_DefaultBackplaneIsNoop()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTenE0Realtime();
        var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IRealtimeBackplane>().Should().BeOfType<NoopRealtimeBackplane>();
    }

    [Fact]
    public void AddTenE0Realtime_RedisBackplane_ThrowsWithoutImplementation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTenE0Realtime(o => o.Backplane = BackplaneMode.Redis);
        var sp = services.BuildServiceProvider();

        var act = () => sp.GetRequiredService<IRealtimeBackplane>();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task NoopRealtimeBackplane_SubscribeHandlerNeverInvoked()
    {
        var backplane = new NoopRealtimeBackplane();
        var invoked = false;
        var sub = backplane.Subscribe(_ => { invoked = true; return Task.CompletedTask; });

        await backplane.PublishAsync(new BackplaneMessage { Delivery = NotificationTarget.Scope.All, EventName = "e" });
        sub.Dispose();

        invoked.Should().BeFalse("Noop backplane never delivers remote messages");
    }

    // 测试用事件
    private sealed record TestNotifyEvent(string UserCode) : INotifyClient
    {
        public NotificationTarget Target => NotificationTarget.User(UserCode, "test.event");
    }
}
