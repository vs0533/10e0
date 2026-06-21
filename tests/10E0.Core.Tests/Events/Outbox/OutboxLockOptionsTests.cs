using TenE0.Core.Events.Outbox;

namespace TenE0.Core.Tests.Events.Outbox;

/// <summary>
/// 单元测试 — OutboxLockOptions 默认值稳定性。
///
/// <para>
/// 关键不变量：<see cref="OutboxLockOptions"/> 是 POCO，默认值必须确定且可预测。
/// 之前实现是 <c>= new OutboxRelayOptions().LockLeaseDuration</c>，会让每个
/// <c>new OutboxLockOptions()</c> 实例拿到不同的随机 <c>LockInstanceId</c>，
/// 破坏"权威来源"语义。本测试钉死这个不变量。
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class OutboxLockOptionsTests
{
    [Fact]
    public void GivenNewOutboxLockOptions_WhenConstructed_ThenDefaultsAreDeterministic()
    {
        // Arrange + Act
        var a = new OutboxLockOptions();
        var b = new OutboxLockOptions();

        // Then — 同一进程内两个实例的默认值必须一致（不依赖任何外部随机源）
        a.LockLeaseDuration.Should().Be(b.LockLeaseDuration,
            "默认值必须确定可预测；不能因为 new 一次就生成新随机 ID");
        a.LockInstanceId.Should().Be(b.LockInstanceId);
    }

    [Fact]
    public void GivenNewOutboxLockOptions_WhenConstructed_ThenLeaseMatchesRelayOptionsDefault()
    {
        // Arrange + Act
        var lockOpts = new OutboxLockOptions();
        var relayOpts = new OutboxRelayOptions();

        // Then — 默认租约时长必须与权威源 (OutboxRelayOptions) 一致
        lockOpts.LockLeaseDuration.Should().Be(relayOpts.LockLeaseDuration,
            "30s 默认值在 OutboxLockOptions 与 OutboxRelayOptions 之间必须保持一致，"
            + "避免 Relay 与锁实现层各自漂移");
    }

    [Fact]
    public void GivenCustomValues_WhenAssigned_ThenExposeThem()
    {
        // Arrange
        var sut = new OutboxLockOptions
        {
            LockLeaseDuration = TimeSpan.FromMinutes(5),
            LockInstanceId = "explicit-instance-id",
        };

        // Then
        sut.LockLeaseDuration.Should().Be(TimeSpan.FromMinutes(5));
        sut.LockInstanceId.Should().Be("explicit-instance-id");
    }
}
