using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using TenE0.Core.Abstractions;
using TenE0.Core.Auth.Jwt.Commands;
using TenE0.Core.Auth.Jwt.Services;
using TenE0.Core.Auth.Jwt.Storage;
using TenE0.Core.Errors;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace TenE0.Core.Tests.Auth.Jwt.Commands;

[Trait("Category", "Unit")]
public sealed class RefreshTokenCommandHandlerTests
{
    private sealed class TestUser : TenE0User { }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TestUser> Users => Set<TestUser>();
        public DbSet<TenE0UserRole> UserRoles => Set<TenE0UserRole>();
        public DbSet<TenE0RefreshToken> RefreshTokens => Set<TenE0RefreshToken>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestUser>(b => { b.HasKey(e => e.Id); b.Property(e => e.UserCode); });
            modelBuilder.Entity<TenE0UserRole>(b => b.HasKey(nameof(TenE0UserRole.UserCode), nameof(TenE0UserRole.RoleCode)));
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
    public async Task HandleAsync_ValidToken_RotatesAndReturnsAuthResult()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var now = DateTimeOffset.UtcNow;
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Users.Add(new TestUser { UserCode = "u001", DisplayName = "张三", PasswordHash = "x", IsActive = true, UserType = UserType.Person });
            ctx.UserRoles.Add(new TenE0UserRole { UserCode = "u001", RoleCode = "user" });
            ctx.RefreshTokens.Add(new TenE0RefreshToken { TokenHash = "oldhash", UserCode = "u001", ExpiresAt = now.AddDays(7), CreatedByIp = "1.2.3.4" });
            await ctx.SaveChangesAsync();
        }

        var timeProvider = new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.Zero };
        timeProvider.SetUtcNow(now.AddHours(1));

        var tokenMock = new Mock<IJwtTokenService>();
        tokenMock.Setup(t => t.HashRefreshToken("valid-refresh")).Returns("oldhash");
        var expiresAt = now.AddHours(2);
        tokenMock.Setup(t => t.Issue("u001", "张三", UserType.Person, It.IsAny<IReadOnlyList<string>>()))
            .Returns(new IssuedTokens("newacc", expiresAt, "newref", "newhash", expiresAt.AddDays(7)));

        var errs = new Errs();
        var logger = NullLogger<RefreshTokenCommandHandler<TestUser, TestDbContext>>.Instance;
        var handler = new RefreshTokenCommandHandler<TestUser, TestDbContext>(factory, tokenMock.Object, timeProvider, errs, logger);

        var result = await handler.HandleAsync(new RefreshTokenCommand("valid-refresh"), CancellationToken.None);

        result.Should().NotBeNull();
        result.AccessToken.Should().Be("newacc");
        result.RefreshToken.Should().Be("newref");
        errs.IsValid.Should().BeTrue();

        await using var verifyCtx = factory.CreateDbContext();
        var oldToken = await verifyCtx.RefreshTokens.SingleAsync(t => t.TokenHash == "oldhash");
        oldToken.RevokedAt.Should().NotBeNull();
        oldToken.ReplacedByTokenHash.Should().Be("newhash");
        var newToken = await verifyCtx.RefreshTokens.SingleAsync(t => t.TokenHash == "newhash");
        newToken.RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_TokenNotFound_ReturnsNullWithError()
    {
        var factory = CreateFactory(Guid.NewGuid().ToString("N"));
        var tokenMock = new Mock<IJwtTokenService>();
        tokenMock.Setup(t => t.HashRefreshToken("bad")).Returns("nohash");
        var errs = new Errs();
        var logger = NullLogger<RefreshTokenCommandHandler<TestUser, TestDbContext>>.Instance;
        var handler = new RefreshTokenCommandHandler<TestUser, TestDbContext>(factory, tokenMock.Object, TimeProvider.System, errs, logger);

        var result = await handler.HandleAsync(new RefreshTokenCommand("bad"), CancellationToken.None);

        result.Should().BeNull();
        errs.GetFirstError().Should().Contain("无效");
    }

    [Fact]
    public async Task HandleAsync_RevokedToken_RevokesAllActive()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var now = DateTimeOffset.UtcNow;
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Users.Add(new TestUser { UserCode = "u001", DisplayName = "X", PasswordHash = "x", IsActive = true, UserType = UserType.Person });
            ctx.RefreshTokens.Add(new TenE0RefreshToken { TokenHash = "revoked", UserCode = "u001", ExpiresAt = now.AddDays(7), RevokedAt = now.AddMinutes(-30) });
            ctx.RefreshTokens.Add(new TenE0RefreshToken { TokenHash = "active1", UserCode = "u001", ExpiresAt = now.AddDays(7) });
            ctx.RefreshTokens.Add(new TenE0RefreshToken { TokenHash = "active2", UserCode = "u001", ExpiresAt = now.AddDays(7) });
            await ctx.SaveChangesAsync();
        }

        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);
        var tokenMock = new Mock<IJwtTokenService>();
        tokenMock.Setup(t => t.HashRefreshToken("replay")).Returns("revoked");
        var errs = new Errs();
        var logger = NullLogger<RefreshTokenCommandHandler<TestUser, TestDbContext>>.Instance;
        var handler = new RefreshTokenCommandHandler<TestUser, TestDbContext>(factory, tokenMock.Object, timeProvider, errs, logger);

        var result = await handler.HandleAsync(new RefreshTokenCommand("replay"), CancellationToken.None);

        result.Should().BeNull();
        errs.GetFirstError().Should().Contain("已撤销");

        await using var verifyCtx = factory.CreateDbContext();
        var allTokens = await verifyCtx.RefreshTokens.ToListAsync();
        allTokens.Should().AllSatisfy(t => t.RevokedAt.Should().NotBeNull());
    }

    [Fact]
    public async Task HandleAsync_ExpiredToken_ReturnsNullWithError()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var now = DateTimeOffset.UtcNow;
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Users.Add(new TestUser { UserCode = "u001", DisplayName = "X", PasswordHash = "x", IsActive = true, UserType = UserType.Person });
            ctx.RefreshTokens.Add(new TenE0RefreshToken { TokenHash = "expired", UserCode = "u001", ExpiresAt = now.AddHours(-1) });
            await ctx.SaveChangesAsync();
        }

        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);
        var tokenMock = new Mock<IJwtTokenService>();
        tokenMock.Setup(t => t.HashRefreshToken("old")).Returns("expired");
        var errs = new Errs();
        var logger = NullLogger<RefreshTokenCommandHandler<TestUser, TestDbContext>>.Instance;
        var handler = new RefreshTokenCommandHandler<TestUser, TestDbContext>(factory, tokenMock.Object, timeProvider, errs, logger);

        var result = await handler.HandleAsync(new RefreshTokenCommand("old"), CancellationToken.None);

        result.Should().BeNull();
        errs.GetFirstError().Should().Contain("过期");
    }

    [Fact]
    public async Task HandleAsync_UserDisabled_ReturnsNullWithError()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var now = DateTimeOffset.UtcNow;
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Users.Add(new TestUser { UserCode = "u001", DisplayName = "X", PasswordHash = "x", IsActive = false, UserType = UserType.Person });
            ctx.RefreshTokens.Add(new TenE0RefreshToken { TokenHash = "hash1", UserCode = "u001", ExpiresAt = now.AddDays(7) });
            await ctx.SaveChangesAsync();
        }

        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);
        var tokenMock = new Mock<IJwtTokenService>();
        tokenMock.Setup(t => t.HashRefreshToken("tok")).Returns("hash1");
        var errs = new Errs();
        var logger = NullLogger<RefreshTokenCommandHandler<TestUser, TestDbContext>>.Instance;
        var handler = new RefreshTokenCommandHandler<TestUser, TestDbContext>(factory, tokenMock.Object, timeProvider, errs, logger);

        var result = await handler.HandleAsync(new RefreshTokenCommand("tok"), CancellationToken.None);

        result.Should().BeNull();
        errs.GetFirstError().Should().Contain("不可用");
    }
}
