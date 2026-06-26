using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using TenE0.Core.Realtime;

namespace TenE0.Core.Tests.Realtime;

[Trait("Category", "Unit")]
public sealed class HubBasedRealtimeNotifierTests
{
    // SignalR 的 IHubContext 链：Clients → IHubClients → (User/Group/All) → IClientProxy.SendAsync
    // 用一个记录所有 SendAsync 调用的 IClientProxy 替身，避免给 4 层接口各写 Mock.Of。
    private sealed class RecordingClientProxy : IClientProxy
    {
        public List<(string Method, object?[] Args)> Calls { get; } = [];
        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
        {
            Calls.Add((method, args));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHubClients : IHubClients
    {
        public RecordingClientProxy UserProxy { get; } = new();
        public RecordingClientProxy GroupProxy { get; } = new();
        public RecordingClientProxy AllProxy { get; } = new();
        public IClientProxy this[string connectionId] => AllProxy;
        public IClientProxy All => AllProxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => AllProxy;
        public IClientProxy Client(string connectionId) => AllProxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => AllProxy;
        public IClientProxy Group(string groupName) => GroupProxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => GroupProxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => GroupProxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken) => GroupProxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames, IReadOnlyList<string> excludedConnectionIds) => GroupProxy;
        public IClientProxy User(string userId) => UserProxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => UserProxy;
    }

    private static IHubContext<NotificationHub> BuildHubContext(FakeHubClients clients)
    {
        var hubContext = new Mock<IHubContext<NotificationHub>>();
        hubContext.SetupGet(c => c.Clients).Returns(clients);
        return hubContext.Object;
    }

    [Fact]
    public async Task NotifyUserAsync_DirectDeliversViaClientsUser_AndPublishesToBackplane()
    {
        var clients = new FakeHubClients();
        var backplane = new Mock<IRealtimeBackplane>();
        var notifier = new HubBasedRealtimeNotifier(BuildHubContext(clients), backplane.Object, NullLogger<HubBasedRealtimeNotifier>.Instance);

        await notifier.NotifyUserAsync("alice", "order.approved", new { OrderId = 42 }, traceId: "trace-1");

        clients.UserProxy.Calls.Should().ContainSingle();
        var call = clients.UserProxy.Calls[0];
        call.Method.Should().Be("order.approved");
        var envelope = call.Args[0].Should().BeOfType<NotificationEnvelope>().Subject;
        envelope.Event.Should().Be("order.approved");
        envelope.TraceId.Should().Be("trace-1");

        backplane.Verify(b => b.PublishAsync(
            It.Is<BackplaneMessage>(m => m.Delivery == NotificationTarget.Scope.User
                && m.Recipient == "alice" && m.EventName == "order.approved"
                && m.TraceId == "trace-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyGroupAsync_DirectDeliversViaClientsGroup()
    {
        var clients = new FakeHubClients();
        var backplane = new Mock<IRealtimeBackplane>();
        var notifier = new HubBasedRealtimeNotifier(BuildHubContext(clients), backplane.Object, NullLogger<HubBasedRealtimeNotifier>.Instance);

        await notifier.NotifyGroupAsync("org:HQ", "broadcast", null);

        clients.GroupProxy.Calls.Should().ContainSingle().Which.Method.Should().Be("broadcast");
        backplane.Verify(b => b.PublishAsync(
            It.Is<BackplaneMessage>(m => m.Delivery == NotificationTarget.Scope.Group && m.Recipient == "org:HQ"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyAllAsync_DirectDeliversViaClientsAll()
    {
        var clients = new FakeHubClients();
        var backplane = new Mock<IRealtimeBackplane>();
        var notifier = new HubBasedRealtimeNotifier(BuildHubContext(clients), backplane.Object, NullLogger<HubBasedRealtimeNotifier>.Instance);

        await notifier.NotifyAllAsync("system", "shutdown");

        clients.AllProxy.Calls.Should().ContainSingle().Which.Method.Should().Be("system");
        backplane.Verify(b => b.PublishAsync(
            It.Is<BackplaneMessage>(m => m.Delivery == NotificationTarget.Scope.All && m.Recipient == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyUserAsync_BackplaneFailure_DoesNotThrow_BestEffort()
    {
        var clients = new FakeHubClients();
        var backplane = new Mock<IRealtimeBackplane>();
        backplane.Setup(b => b.PublishAsync(It.IsAny<BackplaneMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis down"));
        var notifier = new HubBasedRealtimeNotifier(BuildHubContext(clients), backplane.Object, NullLogger<HubBasedRealtimeNotifier>.Instance);

        var act = async () => await notifier.NotifyUserAsync("alice", "e");

        await act.Should().NotThrowAsync();
        // 本地直推仍应执行
        clients.UserProxy.Calls.Should().ContainSingle();
    }
}
