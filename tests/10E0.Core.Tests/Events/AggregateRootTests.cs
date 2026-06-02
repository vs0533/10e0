using TenE0.Core.Events;

namespace TenE0.Core.Tests.Events;

public sealed class AggregateRootTests
{
    [Fact]
    public void InitialState_PendingEvents_Empty()
    {
        // Arrange & Act
        var aggregate = new TestAggregate();

        // Assert
        aggregate.PendingEvents.Should().BeEmpty();
    }

    [Fact]
    public void Raise_ValidEvent_ShouldAddToPending()
    {
        // Arrange
        var aggregate = new TestAggregate();
        var evt = new TestEvent("data1");

        // Act
        aggregate.RaisePublic(evt);

        // Assert
        aggregate.PendingEvents.Should().ContainSingle().Which.Should().BeSameAs(evt);
    }

    [Fact]
    public void Raise_MultipleEvents_ShouldAccumulate()
    {
        // Arrange
        var aggregate = new TestAggregate();
        var evt1 = new TestEvent("data1");
        var evt2 = new TestEvent("data2");
        var evt3 = new TestEvent("data3");

        // Act
        aggregate.RaisePublic(evt1);
        aggregate.RaisePublic(evt2);
        aggregate.RaisePublic(evt3);

        // Assert
        aggregate.PendingEvents.Should().HaveCount(3)
            .And.ContainInOrder(evt1, evt2, evt3);
    }

    [Fact]
    public void Raise_Null_ShouldThrow()
    {
        // Arrange
        var aggregate = new TestAggregate();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => aggregate.RaisePublic(null!));
    }

    [Fact]
    public void ClearEvents_ShouldEmptyList()
    {
        // Arrange
        var aggregate = new TestAggregate();
        aggregate.RaisePublic(new TestEvent("a"));
        aggregate.RaisePublic(new TestEvent("b"));

        // Act
        aggregate.ClearEvents();

        // Assert
        aggregate.PendingEvents.Should().BeEmpty();
    }

    [Fact]
    public void PendingEvents_ShouldBeReadOnly()
    {
        var aggregate = new TestAggregate();
        var pendingEvents = aggregate.PendingEvents;

        var prop = typeof(AggregateRoot).GetProperty(nameof(AggregateRoot.PendingEvents));
        prop.Should().NotBeNull();
        prop!.GetMethod!.ReturnType.Should().Be<IReadOnlyList<IDomainEvent>>();

        pendingEvents.Should().BeAssignableTo<IReadOnlyList<IDomainEvent>>();
    }

    internal sealed class TestAggregate : AggregateRoot
    {
        public void RaisePublic(IDomainEvent evt) => Raise(evt);
    }
    internal sealed record TestEvent(string Data) : IDomainEvent;
}
