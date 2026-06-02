using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using TenE0.Core.Events;
using TenE0.Core.Events.Outbox;

namespace TenE0.Core.Tests.Events.Outbox;

[Trait("Category", "Unit")]
public sealed class OutboxInterceptorTests
{
    private readonly FakeTimeProvider _timeProvider = new();

    private sealed class TestAggregate : AggregateRoot
    {
        public string? Name { get; set; }
        public void RaisePublicEvent(IDomainEvent evt) => Raise(evt);
    }

    private sealed record TestEvent(string Data) : IDomainEvent;

    private sealed record AnotherTestEvent(int Value) : IDomainEvent;

    private sealed class TestDbContext : DbContext
    {
        private readonly OutboxInterceptor _interceptor;

        public DbSet<TestAggregate> TestAggregates => Set<TestAggregate>();
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

        public TestDbContext(DbContextOptions<TestDbContext> options, OutboxInterceptor interceptor)
            : base(options) => _interceptor = interceptor;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            optionsBuilder.AddInterceptors(_interceptor);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestAggregate>(entity =>
            {
                entity.HasKey(e => e.Id);
            });
            modelBuilder.Entity<OutboxMessage>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.EventType).IsRequired();
                entity.Property(e => e.Payload).IsRequired();
            });
        }
    }

    private TestDbContext CreateDbContext()
    {
        var interceptor = new OutboxInterceptor(_timeProvider);
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestDbContext(options, interceptor);
    }

    [Fact]
    public async Task SavingChangesAsync_NoAggregatesWithEvents_NoMessages()
    {
        using var db = CreateDbContext();
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero));

        // Act — has aggregates but none have pending events
        db.TestAggregates.Add(new TestAggregate { Name = "quiet" });
        await db.SaveChangesAsync();

        // Assert — no OutboxMessages created
        var messages = await db.OutboxMessages.ToListAsync();
        messages.Should().BeEmpty();
    }

    [Fact]
    public async Task SavingChangesAsync_SingleAggregate_SingleEvent_CreatesOutboxMessage()
    {
        using var db = CreateDbContext();
        var fixedTime = new DateTimeOffset(2026, 7, 1, 12, 30, 0, TimeSpan.Zero);
        _timeProvider.SetUtcNow(fixedTime);

        var aggregate = new TestAggregate();
        aggregate.RaisePublicEvent(new TestEvent("hello"));

        db.TestAggregates.Add(aggregate);
        await db.SaveChangesAsync();

        var messages = await db.OutboxMessages.ToListAsync();
        messages.Should().ContainSingle();

        var msg = messages[0];
        msg.EventType.Should().Be(typeof(TestEvent).AssemblyQualifiedName);
        msg.OccurredOn.Should().Be(fixedTime);
        msg.SentTime.Should().BeNull();
        msg.AttemptCount.Should().Be(0);

        // Payload should be valid JSON matching the original event
        var deserialized = JsonSerializer.Deserialize<TestEvent>(msg.Payload);
        deserialized.Should().NotBeNull();
        deserialized!.Data.Should().Be("hello");
    }

    [Fact]
    public async Task SavingChangesAsync_MultipleAggregates_MultipleEvents_MessagesPerEvent()
    {
        using var db = CreateDbContext();
        var fixedTime = new DateTimeOffset(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);
        _timeProvider.SetUtcNow(fixedTime);

        var agg1 = new TestAggregate();
        agg1.RaisePublicEvent(new TestEvent("first"));
        agg1.RaisePublicEvent(new AnotherTestEvent(42));

        var agg2 = new TestAggregate();
        agg2.RaisePublicEvent(new TestEvent("second"));

        db.TestAggregates.AddRange(agg1, agg2);
        await db.SaveChangesAsync();

        var messages = await db.OutboxMessages.ToListAsync();
        messages.Should().HaveCount(3);
        messages.Should().AllSatisfy(m => m.OccurredOn.Should().Be(fixedTime));
        messages.Should().AllSatisfy(m => m.SentTime.Should().BeNull());
    }

    [Fact]
    public async Task SavingChangesAsync_ClearsEventsAfterExtraction()
    {
        using var db = CreateDbContext();
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        var aggregate = new TestAggregate();
        aggregate.RaisePublicEvent(new TestEvent("clear-me"));
        aggregate.PendingEvents.Should().HaveCount(1);

        db.TestAggregates.Add(aggregate);
        await db.SaveChangesAsync();

        // PendingEvents should be cleared after extraction
        aggregate.PendingEvents.Should().BeEmpty();
    }

    [Fact]
    public void SavingChanges_Sync_WorksSameAsAsync()
    {
        using var db = CreateDbContext();
        var fixedTime = new DateTimeOffset(2026, 10, 1, 14, 0, 0, TimeSpan.Zero);
        _timeProvider.SetUtcNow(fixedTime);

        var aggregate = new TestAggregate();
        aggregate.RaisePublicEvent(new TestEvent("sync-test"));

        db.TestAggregates.Add(aggregate);

        // Act — synchronous SaveChanges triggers SavingChanges (sync overload)
        db.SaveChanges();

        var messages = db.OutboxMessages.ToList();
        messages.Should().ContainSingle();

        var msg = messages[0];
        msg.EventType.Should().Be(typeof(TestEvent).AssemblyQualifiedName);
        msg.OccurredOn.Should().Be(fixedTime);

        var deserialized = JsonSerializer.Deserialize<TestEvent>(msg.Payload);
        deserialized.Should().NotBeNull();
        deserialized!.Data.Should().Be("sync-test");

        aggregate.PendingEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task SavingChangesAsync_AggregateWithoutEvents_NoMessagesCreated()
    {
        using var db = CreateDbContext();
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));

        var aggregate = new TestAggregate { Name = "no-events" };

        db.TestAggregates.Add(aggregate);
        await db.SaveChangesAsync();

        // Aggregate with no raised events should not create messages
        var messages = await db.OutboxMessages.ToListAsync();
        messages.Should().BeEmpty();

        // PendingEvents should remain empty
        aggregate.PendingEvents.Should().BeEmpty();
    }
}
