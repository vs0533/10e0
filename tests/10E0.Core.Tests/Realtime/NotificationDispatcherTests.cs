using Microsoft.Extensions.Logging.Abstractions;
using TenE0.Core.Realtime;

namespace TenE0.Core.Tests.Realtime;

[Trait("Category", "Unit")]
public sealed class NotificationDispatcherTests
{
    private sealed record UserNotifyEvent(string Code) : INotifyClient
    {
        public NotificationTarget Target => NotificationTarget.User(Code, "user.evt", new { V = 1 });
    }
    private sealed record GroupNotifyEvent(string Group) : INotifyClient
    {
        public NotificationTarget Target => NotificationTarget.Group(Group, "group.evt");
    }
    private sealed record AllNotifyEvent : INotifyClient
    {
        public NotificationTarget Target => NotificationTarget.All("all.evt");
    }

    private static NotificationDispatcher<T> CreateDispatcher<T>(Mock<IRealtimeNotifier> notifier) where T : INotifyClient
        => new(notifier.Object, NullLogger<NotificationDispatcher<T>>.Instance);

    [Fact]
    public async Task HandleAsync_UserTarget_CallsNotifyUserAsync()
    {
        var notifier = new Mock<IRealtimeNotifier>();
        var dispatcher = CreateDispatcher<UserNotifyEvent>(notifier);

        await dispatcher.HandleAsync(new UserNotifyEvent("alice"), default);

        notifier.Verify(n => n.NotifyUserAsync("alice", "user.evt", It.IsAny<object>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        notifier.Verify(n => n.NotifyGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_GroupTarget_CallsNotifyGroupAsync()
    {
        var notifier = new Mock<IRealtimeNotifier>();
        var dispatcher = CreateDispatcher<GroupNotifyEvent>(notifier);

        await dispatcher.HandleAsync(new GroupNotifyEvent("org:HQ"), default);

        notifier.Verify(n => n.NotifyGroupAsync("org:HQ", "group.evt", It.IsAny<object?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_AllTarget_CallsNotifyAllAsync()
    {
        var notifier = new Mock<IRealtimeNotifier>();
        var dispatcher = CreateDispatcher<AllNotifyEvent>(notifier);

        await dispatcher.HandleAsync(new AllNotifyEvent(), default);

        notifier.Verify(n => n.NotifyAllAsync("all.evt", It.IsAny<object?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_NotifierThrows_DoesNotPropagate_BestEffort()
    {
        // 推送失败不应阻塞其它领域事件 handler（fan-out 语义）
        var notifier = new Mock<IRealtimeNotifier>();
        notifier.Setup(n => n.NotifyUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("hub down"));
        var dispatcher = CreateDispatcher<UserNotifyEvent>(notifier);

        var act = async () => await dispatcher.HandleAsync(new UserNotifyEvent("alice"), default);

        await act.Should().NotThrowAsync();
    }
}
