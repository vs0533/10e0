using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TenE0.Core.Auditing;

namespace TenE0.Core.Tests.Auditing;

/// <summary>
/// <see cref="AuditLogSink"/> 单元测试 — Channel 入队 + Enabled 开关 + 时间戳盖戳。
/// </summary>
[Trait("Category", "Unit")]
public sealed class AuditLogSinkTests
{
    private static AuditLogChannel CreateChannel(AuditOptions? options = null)
        => new(Options.Create(options ?? new AuditOptions()));

    private static AuditLogEntry SampleOp() => new()
    {
        EntityType = "Order",
        EntityId = "1",
        Action = "Create",
        ChangedFieldsJson = "[]",
    };

    private static LoginLogEntry SampleLogin() => new()
    {
        UserCode = "alice",
        EventType = "Login",
        Success = true,
    };

    [Fact]
    public async Task EnqueueAsync_WhenEnabled_WritesToChannelAndStampsTime()
    {
        var tp = new FakeTimeProvider();
        var now = new DateTimeOffset(2026, 6, 24, 10, 0, 0, TimeSpan.Zero);
        tp.SetUtcNow(now);
        var channel = CreateChannel();
        var sink = new AuditLogSink(channel, Options.Create(new AuditOptions()), tp);

        var entry = SampleOp();
        await sink.EnqueueAsync(entry);

        channel.Reader.TryRead(out var item).Should().BeTrue();
        var op = item.Should().BeOfType<AuditChannelItem.Op>().Subject;
        op.Entry.Should().BeSameAs(entry);
        op.Entry.CreateTime.Should().Be(now, "Sink 必须用统一时间源盖戳");
    }

    [Fact]
    public async Task WriteLoginAsync_WhenEnabled_WritesLoginItem()
    {
        var channel = CreateChannel();
        var sink = new AuditLogSink(channel, Options.Create(new AuditOptions()), TimeProvider.System);

        await sink.WriteLoginAsync(SampleLogin());

        channel.Reader.TryRead(out var item).Should().BeTrue();
        item.Should().BeOfType<AuditChannelItem.Login>()
            .Which.Entry.UserCode.Should().Be("alice");
    }

    [Fact]
    public async Task EnqueueAsync_WhenDisabled_DoesNothing()
    {
        var channel = CreateChannel();
        var sink = new AuditLogSink(
            channel,
            Options.Create(new AuditOptions { Enabled = false }),
            TimeProvider.System);

        await sink.EnqueueAsync(SampleOp());

        channel.Reader.TryRead(out _).Should().BeFalse("Enabled=false 时不应入队");
    }

    [Fact]
    public async Task EnqueueAsync_ChannelFull_DropsOldestAndDoesNotThrow()
    {
        // 容量 2 + DropOldest：第 3 条入队后最老的第 1 条被丢弃，入队本身不抛异常。
        var options = new AuditOptions { ChannelCapacity = 2, ChannelFullMode = BoundedChannelFullMode.DropOldest };
        var channel = CreateChannel(options);
        var sink = new AuditLogSink(channel, Options.Create(options), TimeProvider.System);

        await sink.EnqueueAsync(SampleOp() with { EntityId = "1" });
        await sink.EnqueueAsync(SampleOp() with { EntityId = "2" });
        await sink.EnqueueAsync(SampleOp() with { EntityId = "3" });

        // Channel 应只剩 2 条（DropOldest 丢掉 Id=1）
        var remaining = new List<AuditChannelItem>();
        while (channel.Reader.TryRead(out var item)) remaining.Add(item);
        remaining.Should().HaveCount(2);
        remaining.OfType<AuditChannelItem.Op>()
            .Select(o => o.Entry.EntityId)
            .Should().Equal(["2", "3"], "Id=1 被丢弃");
    }

    [Fact]
    public async Task EnqueueAsync_ChannelCompleted_ReturnsFalseAndDoesNotThrow()
    {
        var channel = CreateChannel();
        channel.Complete(); // 模拟停机
        var sink = new AuditLogSink(channel, Options.Create(new AuditOptions()), TimeProvider.System);

        // 停机后入队应静默丢弃，不抛异常（best-effort 契约）
        var act = async () => await sink.EnqueueAsync(SampleOp());
        await act.Should().NotThrowAsync();
    }
}
