using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using TenE0.Core.Abstractions;
using TenE0.Core.Cqrs.Behaviors;

namespace TenE0.Core.Tests.Cqrs.Behaviors;

[Trait("Category", "Unit")]
public sealed class TransactionBehaviorTests
{
    public record NonTransactionalCmd : ICommand<Unit>;
    public record TransactionalCmd : ICommand<Unit>, ITransactional;
    public record FaultyTransactionalCmd : ICommand<Unit>, ITransactional;

    public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            optionsBuilder.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        }
    }

    private static IDbContextFactory<TestDbContext> CreateFactory(string dbName)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TestDbContextFactory(options);
    }

    private sealed class TestDbContextFactory(DbContextOptions<TestDbContext> options)
        : IDbContextFactory<TestDbContext>
    {
        public TestDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task NonTransactionalCommand_PassesThrough_WithoutTransaction()
    {
        var factory = CreateFactory("passthrough");
        var logger = NullLogger<TransactionBehavior<NonTransactionalCmd, Unit, TestDbContext>>.Instance;
        var behavior = new TransactionBehavior<NonTransactionalCmd, Unit, TestDbContext>(factory, logger);
        var nextCalled = false;

        await behavior.HandleAsync(
            new NonTransactionalCmd(),
            _ => { nextCalled = true; return Unit.Task; },
            CancellationToken.None);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task TransactionalCommand_CallsNext_WithinTransaction()
    {
        var factory = CreateFactory("commit");
        var logger = NullLogger<TransactionBehavior<TransactionalCmd, Unit, TestDbContext>>.Instance;
        var behavior = new TransactionBehavior<TransactionalCmd, Unit, TestDbContext>(factory, logger);
        var nextCalled = false;

        await behavior.HandleAsync(
            new TransactionalCmd(),
            _ => { nextCalled = true; return Unit.Task; },
            CancellationToken.None);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task TransactionalCommand_PropagatesException_OnHandlerFailure()
    {
        var factory = CreateFactory("rollback");
        var logger = NullLogger<TransactionBehavior<FaultyTransactionalCmd, Unit, TestDbContext>>.Instance;
        var behavior = new TransactionBehavior<FaultyTransactionalCmd, Unit, TestDbContext>(factory, logger);

        var act = () => behavior.HandleAsync(
            new FaultyTransactionalCmd(),
            _ => throw new InvalidOperationException("handler failure"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("handler failure");
    }

    [Fact]
    public async Task TransactionalCommand_AllowsMultipleHandlers_WithoutConflict()
    {
        var factory = CreateFactory("multiple");
        var logger = NullLogger<TransactionBehavior<TransactionalCmd, Unit, TestDbContext>>.Instance;
        var behavior = new TransactionBehavior<TransactionalCmd, Unit, TestDbContext>(factory, logger);
        var count = 0;

        await behavior.HandleAsync(new TransactionalCmd(), _ => { count++; return Unit.Task; }, CancellationToken.None);
        await behavior.HandleAsync(new TransactionalCmd(), _ => { count++; return Unit.Task; }, CancellationToken.None);

        count.Should().Be(2);
    }

    [Fact]
    public async Task NonTransactional_SkipsDbContextCreation_WhenStrictMockUsed()
    {
        var logger = NullLogger<TransactionBehavior<NonTransactionalCmd, Unit, TestDbContext>>.Instance;
        var strictMock = new Moq.Mock<IDbContextFactory<TestDbContext>>(Moq.MockBehavior.Strict);
        // Non-transactional commands never create a DbContext — strict mock proves this
        var behavior = new TransactionBehavior<NonTransactionalCmd, Unit, TestDbContext>(strictMock.Object, logger);
        var nextCalled = false;

        await behavior.HandleAsync(
            new NonTransactionalCmd(),
            _ => { nextCalled = true; return Unit.Task; },
            CancellationToken.None);

        nextCalled.Should().BeTrue();
    }
}
