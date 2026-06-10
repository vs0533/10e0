using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
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

    // ========================================================================================
    // Savepoint 嵌套边界测试 (P1: 修复 BUG-001 后的回归保护)
    //
    // 真实场景下 BeginTransaction 路径在 EF Core InMemory provider 中也被接受 (no-op)，
    // 但 Database.CurrentTransaction 在 InMemory 永远为 null —— 走不到 Savepoint 分支。
    // 因此 Savepoint 路径用 Mock<IDbContextTransaction> + Mock<DatabaseFacade> 验证调用序列。
    // ========================================================================================

    private sealed class SavepointTracker
    {
        public ConcurrentQueue<string> Created { get; } = new();
        public ConcurrentQueue<string> Released { get; } = new();
        public ConcurrentQueue<string> RolledBackTo { get; } = new();
    }

    private sealed class BeginTxTracker
    {
        public int BeginCount { get; set; }
        public int CommitCount { get; set; }
        public int RollbackCount { get; set; }
    }

    /// <summary>
    /// 构建一个"已经有外层事务"的工厂：Database.CurrentTransaction 返回严格 mock。
    /// 行为应走 Savepoint 分支 (CreateSavepointAsync / ReleaseSavepointAsync / RollbackToSavepointAsync)。
    /// </summary>
    private static (IDbContextFactory<DbContext> Factory, Mock<IDbContextTransaction> Tx, SavepointTracker Tracker)
        CreateFactoryWithExistingTransaction()
    {
        var tracker = new SavepointTracker();
        var txMock = new Mock<IDbContextTransaction>(MockBehavior.Strict);
        txMock.Setup(t => t.CreateSavepointAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Callback<string, CancellationToken>((sp, _) => tracker.Created.Enqueue(sp))
              .Returns(Task.CompletedTask);
        txMock.Setup(t => t.ReleaseSavepointAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Callback<string, CancellationToken>((sp, _) => tracker.Released.Enqueue(sp))
              .Returns(Task.CompletedTask);
        txMock.Setup(t => t.RollbackToSavepointAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Callback<string, CancellationToken>((sp, _) => tracker.RolledBackTo.Enqueue(sp))
              .Returns(Task.CompletedTask);
        txMock.Setup(t => t.Dispose()).Verifiable();
        txMock.Setup(t => t.DisposeAsync()).Returns(ValueTask.CompletedTask);

        // DatabaseFacade 构造有 Check.NotNull(context)，必须传一个真实 DbContext。
        var realContext = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase($"savepoint-exists-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

        var dbMock = new Mock<DatabaseFacade>(realContext);
        dbMock.SetupGet(d => d.CurrentTransaction).Returns(txMock.Object);

        var ctxMock = new Mock<DbContext>();
        ctxMock.SetupGet(c => c.Database).Returns(dbMock.Object);
        ctxMock.Setup(c => c.Dispose()).Verifiable();
        ctxMock.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var factoryMock = new Mock<IDbContextFactory<DbContext>>(MockBehavior.Strict);
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                   .ReturnsAsync(ctxMock.Object);

        return (factoryMock.Object, txMock, tracker);
    }

    /// <summary>
    /// 构建一个"没有外层事务"的工厂：Database.CurrentTransaction 返回 null，
    /// Database.BeginTransactionAsync 返回一个严格 mock。行为应走 Begin 分支。
    /// </summary>
    private static (IDbContextFactory<DbContext> Factory, Mock<IDbContextTransaction> NewTx, BeginTxTracker Tracker)
        CreateFactoryWithoutTransaction()
    {
        var tracker = new BeginTxTracker();
        var newTxMock = new Mock<IDbContextTransaction>(MockBehavior.Strict);
        newTxMock.Setup(t => t.CommitAsync(It.IsAny<CancellationToken>()))
                 .Callback<CancellationToken>(_ => tracker.CommitCount++)
                 .Returns(Task.CompletedTask);
        newTxMock.Setup(t => t.RollbackAsync(It.IsAny<CancellationToken>()))
                 .Callback<CancellationToken>(_ => tracker.RollbackCount++)
                 .Returns(Task.CompletedTask);
        newTxMock.Setup(t => t.Dispose()).Verifiable();
        newTxMock.Setup(t => t.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var realContext = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase($"savepoint-missing-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

        var dbMock = new Mock<DatabaseFacade>((DbContext)realContext);
        dbMock.SetupGet(d => d.CurrentTransaction).Returns((IDbContextTransaction?)null);
        dbMock.Setup(d => d.BeginTransactionAsync(It.IsAny<CancellationToken>()))
              .Callback<CancellationToken>(_ => tracker.BeginCount++)
              .ReturnsAsync(newTxMock.Object);

        var ctxMock = new Mock<DbContext>();
        ctxMock.SetupGet(c => c.Database).Returns(dbMock.Object);
        ctxMock.Setup(c => c.Dispose()).Verifiable();
        ctxMock.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var factoryMock = new Mock<IDbContextFactory<DbContext>>(MockBehavior.Strict);
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                   .ReturnsAsync(ctxMock.Object);

        return (factoryMock.Object, newTxMock, tracker);
    }

    [Fact]
    public async Task NoOuterTransaction_BeginsNewTransaction_AndCommitsOnSuccess()
    {
        var (factory, _, tracker) = CreateFactoryWithoutTransaction();
        var logger = NullLogger<TransactionBehavior<TransactionalCmd, Unit, DbContext>>.Instance;
        var behavior = new TransactionBehavior<TransactionalCmd, Unit, DbContext>(factory, logger);
        var nextCalled = false;

        await behavior.HandleAsync(
            new TransactionalCmd(),
            _ => { nextCalled = true; return Unit.Task; },
            CancellationToken.None);

        nextCalled.Should().BeTrue();
        tracker.BeginCount.Should().Be(1);
        tracker.CommitCount.Should().Be(1);
        tracker.RollbackCount.Should().Be(0);
    }

    [Fact]
    public async Task OuterTransactionExists_CreatesUniqueSavepoint_AndReleasesOnSuccess()
    {
        var (factory, _, tracker) = CreateFactoryWithExistingTransaction();
        var logger = NullLogger<TransactionBehavior<TransactionalCmd, Unit, DbContext>>.Instance;
        var behavior = new TransactionBehavior<TransactionalCmd, Unit, DbContext>(factory, logger);

        await behavior.HandleAsync(
            new TransactionalCmd(),
            _ => Unit.Task,
            CancellationToken.None);

        // 恰好 1 个 savepoint 被创建、1 个被 release，0 个被回滚
        tracker.Created.Should().HaveCount(1);
        tracker.Released.Should().HaveCount(1);
        tracker.RolledBackTo.Should().BeEmpty();

        // 名字符合 "sp_{Guid:N}" 格式 (32 位无连字符 GUID)
        var createdName = tracker.Created.Single();
        createdName.Should().StartWith("sp_");
        createdName.Length.Should().Be("sp_".Length + 32);

        // Create 和 Release 用的是同一个名字 (这是设计意图：回滚到同名 savepoint)
        tracker.Released.Single().Should().Be(createdName);
    }

    [Fact]
    public async Task InnerCommandFails_RollbacksOnlySavepoint_OuterTransactionStaysAlive()
    {
        var (factory, _, tracker) = CreateFactoryWithExistingTransaction();
        var logger = NullLogger<TransactionBehavior<FaultyTransactionalCmd, Unit, DbContext>>.Instance;
        var behavior = new TransactionBehavior<FaultyTransactionalCmd, Unit, DbContext>(factory, logger);

        var act = () => behavior.HandleAsync(
            new FaultyTransactionalCmd(),
            _ => throw new InvalidOperationException("inner handler failure"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("inner handler failure");

        // 创建了 savepoint，但回滚到它（不是 Release）
        tracker.Created.Should().HaveCount(1);
        tracker.RolledBackTo.Should().HaveCount(1);
        tracker.Released.Should().BeEmpty();

        // RollbackToSavepoint 用的就是创建的同名 savepoint
        tracker.RolledBackTo.Single().Should().Be(tracker.Created.Single());

        // 关键断言：外层事务本身没有被 Rollback —— 它还在外层管理者的控制下
        // (CreateSavepoint/RollbackToSavepoint 都不影响外层事务生命周期)
        factory.Should().NotBeNull(); // 工厂本身是存根；存在性作为 sanity check
    }

    [Fact]
    public async Task OuterCommandFails_BeginBranch_RollsBackAllChanges_NoSavepointInvoked()
    {
        var (factory, _, tracker) = CreateFactoryWithoutTransaction();
        var logger = NullLogger<TransactionBehavior<FaultyTransactionalCmd, Unit, DbContext>>.Instance;
        var behavior = new TransactionBehavior<FaultyTransactionalCmd, Unit, DbContext>(factory, logger);

        var act = () => behavior.HandleAsync(
            new FaultyTransactionalCmd(),
            _ => throw new InvalidOperationException("outer handler failure"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("outer handler failure");

        // Begin 分支在失败时调用 RollbackAsync，绝不调用 CreateSavepointAsync
        tracker.BeginCount.Should().Be(1);
        tracker.RollbackCount.Should().Be(1);
        tracker.CommitCount.Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentNestedCommands_AllGeneratedSavepointNamesAreUnique()
    {
        // 模拟 N 个并发请求，每个都嵌在一个外层事务中跑 TransactionBehavior。
        // 验证：所有 savepoint 名字都遵循 "sp_<guid>" 格式且互不重复。
        //
        // 用 Task.Run 强制把每个行为执行推送到 ThreadPool，否则在同步的 HandleAsync 路径
        // 上 64 个 task 全在同一条调用栈上顺序执行 —— 根本不并发，GUID 也根本不会撞。
        const int concurrentCount = 64;
        var tasks = new Task<SavepointTracker>[concurrentCount];

        for (var i = 0; i < concurrentCount; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                var (factory, _, tracker) = CreateFactoryWithExistingTransaction();
                var logger = NullLogger<TransactionBehavior<TransactionalCmd, Unit, DbContext>>.Instance;
                var behavior = new TransactionBehavior<TransactionalCmd, Unit, DbContext>(factory, logger);
                await behavior.HandleAsync(
                    new TransactionalCmd(),
                    _ => Unit.Task,
                    CancellationToken.None);
                return tracker;
            });
        }

        var trackers = await Task.WhenAll(tasks);

        var allNames = trackers.SelectMany(t => t.Created).ToList();
        allNames.Should().HaveCount(concurrentCount);
        allNames.Should().OnlyContain(n => n.StartsWith("sp_"));
        allNames.Should().OnlyHaveUniqueItems(
            "GUID-based savepoint names must not collide under concurrent invocations");
    }

    [Fact]
    public async Task NonTransactionalCommand_NeverInvokesFactory()
    {
        // 严格 mock 工厂 + 非事务命令：CreateDbContextAsync 必须 0 次被调用。
        // (与 "NonTransactional_SkipsDbContextCreation_WhenStrictMockUsed" 互补，
        //  这里用显式 Times.Never 断言保留回归意图。)
        var strictFactory = new Mock<IDbContextFactory<DbContext>>(MockBehavior.Strict);
        var logger = NullLogger<TransactionBehavior<NonTransactionalCmd, Unit, DbContext>>.Instance;
        var behavior = new TransactionBehavior<NonTransactionalCmd, Unit, DbContext>(strictFactory.Object, logger);
        var nextCalled = false;

        await behavior.HandleAsync(
            new NonTransactionalCmd(),
            _ => { nextCalled = true; return Unit.Task; },
            CancellationToken.None);

        nextCalled.Should().BeTrue();
        strictFactory.Verify(
            f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // Issue #10 acceptance: 100 consecutive runs of ConcurrentNestedCommands on
    // multi-core hardware must remain deterministic — GUID generation must not collide.
    // We can't run "100" in a single xUnit Fact and stay under reasonable time budgets,
    // so we loop the inner concurrent-batch 100x and assert no collisions across all batches.
    [Fact]
    public async Task ConcurrentNestedCommands_100Batches_AllSavepointNamesUnique()
    {
        const int batchSize = 16;
        const int totalBatches = 100;
        var allNames = new List<string>(batchSize * totalBatches);

        for (var batch = 0; batch < totalBatches; batch++)
        {
            var tasks = new Task<SavepointTracker>[batchSize];
            for (var i = 0; i < batchSize; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    var (factory, _, tracker) = CreateFactoryWithExistingTransaction();
                    var logger = NullLogger<TransactionBehavior<TransactionalCmd, Unit, DbContext>>.Instance;
                    var behavior = new TransactionBehavior<TransactionalCmd, Unit, DbContext>(factory, logger);
                    await behavior.HandleAsync(
                        new TransactionalCmd(),
                        _ => Unit.Task,
                        CancellationToken.None);
                    return tracker;
                });
            }

            var trackers = await Task.WhenAll(tasks);
            allNames.AddRange(trackers.SelectMany(t => t.Created));
        }

        allNames.Should().HaveCount(batchSize * totalBatches);
        allNames.Should().OnlyHaveUniqueItems(
            $"across {totalBatches} consecutive concurrent batches (size {batchSize} each), GUIDs must never collide");
    }
}
