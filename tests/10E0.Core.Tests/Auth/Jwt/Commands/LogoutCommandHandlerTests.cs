using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TenE0.Core.Abstractions;
using TenE0.Core.Auth.Jwt.Commands;
using TenE0.Core.Auth.Jwt.Services;
using TenE0.Core.Auth.Jwt.Storage;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace TenE0.Core.Tests.Auth.Jwt.Commands;

[Trait("Category", "Unit")]
public sealed class LogoutCommandHandlerTests
{
    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TenE0RefreshToken> RefreshTokens => Set<TenE0RefreshToken>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TenE0RefreshToken>(b => b.HasKey(e => e.Id));
        }
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            optionsBuilder.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<TestDbContext> options) : IDbContextFactory<TestDbContext>
    {
        public TestDbContext CreateDbContext() => new(options);
    }

    private static IDbContextFactory<TestDbContext> CreateFactory(string dbName)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TestDbContextFactory(options);
    }

    [Fact]
    public async Task HandleAsync_ExistingToken_RevokesIt()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.RefreshTokens.Add(new TenE0RefreshToken
            {
                TokenHash = "hash1", UserCode = "u001",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
            });
            await ctx.SaveChangesAsync();
        }

        var timeProvider = new FakeTimeProvider();
        var now = DateTimeOffset.UtcNow;
        timeProvider.SetUtcNow(now);

        var tokenMock = new Mock<IJwtTokenService>();
        tokenMock.Setup(t => t.HashRefreshToken("mytoken")).Returns("hash1");

        var handler = new LogoutCommandHandler<TestDbContext>(factory, tokenMock.Object, timeProvider);

        var result = await handler.HandleAsync(new LogoutCommand("mytoken"), CancellationToken.None);

        result.Should().Be(Unit.Value);

        await using var verifyCtx = factory.CreateDbContext();
        var record = await verifyCtx.RefreshTokens.SingleAsync();
        record.RevokedAt.Should().Be(now);
    }

    [Fact]
    public async Task HandleAsync_AlreadyRevoked_ReturnsUnit()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.RefreshTokens.Add(new TenE0RefreshToken
            {
                TokenHash = "hash1", UserCode = "u001",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
                RevokedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
            });
            await ctx.SaveChangesAsync();
        }

        var tokenMock = new Mock<IJwtTokenService>();
        tokenMock.Setup(t => t.HashRefreshToken("mytoken")).Returns("hash1");

        var handler = new LogoutCommandHandler<TestDbContext>(factory, tokenMock.Object, TimeProvider.System);

        var result = await handler.HandleAsync(new LogoutCommand("mytoken"), CancellationToken.None);

        result.Should().Be(Unit.Value);
    }

    [Fact]
    public async Task HandleAsync_NonExistentToken_ReturnsUnit()
    {
        var factory = CreateFactory(Guid.NewGuid().ToString("N"));
        var tokenMock = new Mock<IJwtTokenService>();
        tokenMock.Setup(t => t.HashRefreshToken("ghost")).Returns("nohash");

        var handler = new LogoutCommandHandler<TestDbContext>(factory, tokenMock.Object, TimeProvider.System);

        var result = await handler.HandleAsync(new LogoutCommand("ghost"), CancellationToken.None);

        result.Should().Be(Unit.Value);
    }
}
