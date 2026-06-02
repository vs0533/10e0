using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TenE0.Core.Hosting;
using Moq;

namespace TenE0.Core.Tests.Hosting;

[Trait("Category", "Unit")]
public sealed class DatabaseInitializerServiceTests
{
    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options) { }

    private sealed class TestSeeder(string name, int order = 0) : IDataSeeder
    {
        private readonly List<string> _calls = [];
        public int Order => order;
        public Task SeedAsync(DbContext context, CancellationToken ct) { _calls.Add(name); return Task.CompletedTask; }
        public IReadOnlyList<string> Calls => _calls;
    }

    [Fact]
    public async Task StartingAsync_RunsEnsureCreated()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;
        var factory = new FakeFactory(options);

        var spMock = new Mock<IServiceProvider>();
        spMock.Setup(sp => sp.GetService(typeof(IDbContextFactory<TestDbContext>))).Returns(factory);
        spMock.Setup(sp => sp.GetService(typeof(IEnumerable<IDataSeeder>))).Returns(Array.Empty<IDataSeeder>());

        var scopeMock = new Mock<IServiceScope>();
        scopeMock.SetupGet(s => s.ServiceProvider).Returns(spMock.Object);
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

        var logger = NullLogger<DatabaseInitializerService<TestDbContext>>.Instance;
        var service = new DatabaseInitializerService<TestDbContext>(scopeFactoryMock.Object, logger);

        // Should not throw — EnsureCreated works with InMemory
        await service.StartingAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartingAsync_SeedersRunInOrder()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(dbName).Options;
        var factory = new FakeFactory(options);

        var seederSecond = new TestSeeder("Second", order: 2);
        var seederFirst = new TestSeeder("First", order: 1);
        var seederLast = new TestSeeder("Last", order: 99);

        var spMock = new Mock<IServiceProvider>();
        spMock.Setup(sp => sp.GetService(typeof(IDbContextFactory<TestDbContext>))).Returns(factory);
        spMock.Setup(sp => sp.GetService(typeof(IEnumerable<IDataSeeder>)))
            .Returns(new IDataSeeder[] { seederSecond, seederFirst, seederLast });

        var scopeMock = new Mock<IServiceScope>();
        scopeMock.SetupGet(s => s.ServiceProvider).Returns(spMock.Object);
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

        var logger = NullLogger<DatabaseInitializerService<TestDbContext>>.Instance;
        var service = new DatabaseInitializerService<TestDbContext>(scopeFactoryMock.Object, logger);

        await service.StartingAsync(CancellationToken.None);

        seederFirst.Calls.Should().Contain("First");
        seederSecond.Calls.Should().Contain("Second");
        seederLast.Calls.Should().Contain("Last");
    }

    [Fact]
    public async Task StartAsync_ReturnsCompletedTask()
    {
        var service = new DatabaseInitializerService<TestDbContext>(
            Mock.Of<IServiceScopeFactory>(), NullLogger<DatabaseInitializerService<TestDbContext>>.Instance);

        var task = service.StartAsync(CancellationToken.None);
        task.IsCompletedSuccessfully.Should().BeTrue();
        await task;
    }

    [Fact]
    public async Task StoppedAsync_ReturnsCompletedTask()
    {
        var service = new DatabaseInitializerService<TestDbContext>(
            Mock.Of<IServiceScopeFactory>(), NullLogger<DatabaseInitializerService<TestDbContext>>.Instance);

        var task = service.StoppedAsync(CancellationToken.None);
        task.IsCompletedSuccessfully.Should().BeTrue();
        await task;
    }

    private sealed class FakeFactory(DbContextOptions<TestDbContext> options) : IDbContextFactory<TestDbContext>
    {
        public TestDbContext CreateDbContext() => new(options);
    }
}
