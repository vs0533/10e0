using Microsoft.EntityFrameworkCore.Diagnostics;
using TenE0.Core.Abstractions;
using TenE0.Core.Auth.Jwt.Commands;
using TenE0.Core.Auth.Jwt.Services;
using TenE0.Core.Auth.Jwt.Storage;
using TenE0.Core.Errors;
using TenE0.Core.Permissions.Storage;

namespace TenE0.Core.Tests.Auth.Jwt.Commands;

[Trait("Category", "Unit")]
public sealed class LoginCommandHandlerTests
{
    private sealed class TestUser : TenE0User { }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TestUser> Users => Set<TestUser>();
        public DbSet<TenE0UserRole> UserRoles => Set<TenE0UserRole>();
        public DbSet<TenE0RefreshToken> RefreshTokens => Set<TenE0RefreshToken>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestUser>(b => { b.HasKey(e => e.Id); b.Property(e => e.UserCode); b.Property(e => e.PasswordHash); });
            modelBuilder.Entity<TenE0UserRole>(b => b.HasKey(nameof(TenE0UserRole.UserCode), nameof(TenE0UserRole.RoleCode)));
            modelBuilder.Entity<TenE0RefreshToken>(b => b.HasKey(e => e.Id));
            // #7: role version 需要 TenE0Role 注册才能查询
            modelBuilder.Entity<TenE0Role>(b => b.HasKey(r => r.Code));
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
    public async Task HandleAsync_ValidCredentials_ReturnsAuthResult()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Users.Add(new TestUser { UserCode = "u001", DisplayName = "张三", PasswordHash = "hash123", IsActive = true, UserType = UserType.Person });
            ctx.UserRoles.Add(new TenE0UserRole { UserCode = "u001", RoleCode = "admin" });
            await ctx.SaveChangesAsync();
        }

        var pwMock = new Mock<IPasswordHasher>();
        pwMock.Setup(p => p.Verify("pass", "hash123")).Returns(true);

        var tokenMock = new Mock<IJwtTokenService>();
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        tokenMock.Setup(t => t.Issue("u001", "张三", UserType.Person, It.Is<IReadOnlyList<string>>(r => r.Contains("admin")), It.IsAny<IReadOnlyDictionary<string, long>>()))
            .Returns(new IssuedTokens("acctok", expiresAt, "reftok", "refhash", expiresAt.AddDays(7)));

        var errs = new Errs();
        var handler = new LoginCommandHandler<TestUser, TestDbContext>(factory, pwMock.Object, tokenMock.Object, errs);

        var result = await handler.HandleAsync(new LoginCommand("u001", "pass"), CancellationToken.None);

        result.Should().NotBeNull();
        result.AccessToken.Should().Be("acctok");
        result.RefreshToken.Should().Be("reftok");
        result.UserCode.Should().Be("u001");
        result.Roles.Should().Contain("admin");
        errs.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_InvalidPassword_ReturnsNullWithError()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Users.Add(new TestUser { UserCode = "u001", DisplayName = "张三", PasswordHash = "hash123", IsActive = true, UserType = UserType.Person });
            await ctx.SaveChangesAsync();
        }

        var pwMock = new Mock<IPasswordHasher>();
        pwMock.Setup(p => p.Verify("wrong", "hash123")).Returns(false);
        var tokenMock = new Mock<IJwtTokenService>();
        var errs = new Errs();
        var handler = new LoginCommandHandler<TestUser, TestDbContext>(factory, pwMock.Object, tokenMock.Object, errs);

        var result = await handler.HandleAsync(new LoginCommand("u001", "wrong"), CancellationToken.None);

        result.Should().BeNull();
        errs.IsValid.Should().BeFalse();
        errs.GetFirstError().Should().Contain("用户名或密码错误");
    }

    [Fact]
    public async Task HandleAsync_NonExistentUser_ReturnsNullWithError()
    {
        var factory = CreateFactory(Guid.NewGuid().ToString("N"));
        var pwMock = new Mock<IPasswordHasher>();
        pwMock.Setup(p => p.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(false);
        var tokenMock = new Mock<IJwtTokenService>();
        var errs = new Errs();
        var handler = new LoginCommandHandler<TestUser, TestDbContext>(factory, pwMock.Object, tokenMock.Object, errs);

        var result = await handler.HandleAsync(new LoginCommand("ghost", "any"), CancellationToken.None);

        result.Should().BeNull();
        errs.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_DisabledUser_ReturnsNullWithError()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Users.Add(new TestUser { UserCode = "u001", DisplayName = "张三", PasswordHash = "hash123", IsActive = false, UserType = UserType.Person });
            await ctx.SaveChangesAsync();
        }

        var pwMock = new Mock<IPasswordHasher>();
        pwMock.Setup(p => p.Verify("pass", "hash123")).Returns(true);
        var tokenMock = new Mock<IJwtTokenService>();
        var errs = new Errs();
        var handler = new LoginCommandHandler<TestUser, TestDbContext>(factory, pwMock.Object, tokenMock.Object, errs);

        var result = await handler.HandleAsync(new LoginCommand("u001", "pass"), CancellationToken.None);

        result.Should().BeNull();
        errs.IsValid.Should().BeFalse();
        errs.GetFirstError().Should().Contain("禁用");
    }
}
