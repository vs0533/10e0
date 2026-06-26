using Microsoft.EntityFrameworkCore.Diagnostics;
using TenE0.Core.Abstractions;
using TenE0.Core.Auditing;
using TenE0.Core.Auth.Jwt.Commands;
using TenE0.Core.Auth.Jwt.Services;
using TenE0.Core.Auth.Jwt.Storage;
using TenE0.Core.Errors;
using TenE0.Core.Permissions.Storage;

namespace TenE0.Core.Auth.Jwt.Tests.Commands;

/// <summary>
/// BDD acceptance tests for #97 — verifies that <see cref="LoginCommandHandler{TUser, TContext}"/>
/// always invokes <see cref="IPasswordHasher.Verify"/> exactly once per login attempt, even when
/// the user does not exist. This closes the user-enumeration timing oracle that arises from
/// the <c>&amp;&amp;</c> short-circuit in the original implementation.
///
/// 业务规则：无论用户名是否存在、密码是否匹配，password hasher 必须执行等量运算，
/// 攻击者无法通过响应时间差异区分「用户不存在」与「密码错误」。
/// </summary>
[Trait("Category", "BDD")]
public sealed class LoginTimingAttackPreventionAcceptanceTests
{
    private const string NonExistentUserCode = "ghost";
    private const string AnyPassword = "any-password-attempt";

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

    private sealed record HandlerBundle(
        LoginCommandHandler<TestUser, TestDbContext> Handler,
        Mock<IPasswordHasher> PasswordHasherMock,
        Mock<IJwtTokenService> TokenServiceMock,
        Errs Errs);

    private static HandlerBundle CreateHandler(
        IDbContextFactory<TestDbContext> factory,
        Mock<IPasswordHasher> pwMock)
    {
        var tokenMock = new Mock<IJwtTokenService>();
        var errs = new Errs();
        var handler = new LoginCommandHandler<TestUser, TestDbContext>(factory, pwMock.Object, tokenMock.Object, errs, new NullAuditLogSink());
        return new HandlerBundle(handler, pwMock, tokenMock, errs);
    }

    // ── Acceptance #97-1: 找不到用户时仍执行一次 Verify（核心场景） ──

    [Fact]
    public async Task GivenNonExistentUser_WhenHandlingLogin_ThenPasswordHasherVerifiesExactlyOnce()
    {
        // Arrange — DB 中没有任何用户
        var factory = CreateFactory(Guid.NewGuid().ToString("N"));
        var pwMock = new Mock<IPasswordHasher>();
        pwMock.Setup(p => p.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(false);
        var bundle = CreateHandler(factory, pwMock);

        // Act — 尝试登录一个不存在的用户
        var result = await bundle.Handler.HandleAsync(new LoginCommand(NonExistentUserCode, AnyPassword), CancellationToken.None);

        // Assert — 业务结果
        result.Should().BeNull();
        bundle.Errs.IsValid.Should().BeFalse();
        bundle.Errs.GetFirstError().Should().Contain("用户名或密码错误");

        // Assert — 核心安全断言：Verify 仍然被调用一次（防止 timing oracle）
        bundle.PasswordHasherMock.Verify(
            p => p.Verify(AnyPassword, It.IsAny<string>()),
            Times.Once,
            "登录处理器必须在用户不存在时仍执行一次 password verify，防止通过响应时间差异枚举有效用户名");
    }

    // ── Acceptance #97-2: 找不到用户时 Verify 必须接收密码原文（短路不能跳过 password 读取） ──

    [Fact]
    public async Task GivenNonExistentUser_WhenHandlingLogin_ThenVerifyReceivesTheSubmittedPassword()
    {
        // Arrange
        var factory = CreateFactory(Guid.NewGuid().ToString("N"));
        var pwMock = new Mock<IPasswordHasher>();
        pwMock.Setup(p => p.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(false);
        var bundle = CreateHandler(factory, pwMock);

        // Act
        _ = await bundle.Handler.HandleAsync(new LoginCommand(NonExistentUserCode, AnyPassword), CancellationToken.None);

        // Assert — Verify 的第一个实参必须是用户提交的明文密码
        bundle.PasswordHasherMock.Verify(p => p.Verify(AnyPassword, It.IsAny<string>()), Times.Once);
    }

    // ── Acceptance #97-3: 用户存在但密码错误时 Verify 也只跑一次（与 #97-1 时序对齐） ──

    [Fact]
    public async Task GivenExistingUserWithWrongPassword_WhenHandlingLogin_ThenPasswordHasherVerifiesExactlyOnce()
    {
        // Arrange — DB 中有一个用户，但提交的密码错误
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Users.Add(new TestUser
            {
                UserCode = "u001",
                DisplayName = "张三",
                PasswordHash = "real-hash",
                IsActive = true,
                UserType = UserType.Person,
            });
            await ctx.SaveChangesAsync();
        }

        var pwMock = new Mock<IPasswordHasher>();
        pwMock.Setup(p => p.Verify("wrong-pass", "real-hash")).Returns(false);
        var bundle = CreateHandler(factory, pwMock);

        // Act
        var result = await bundle.Handler.HandleAsync(new LoginCommand("u001", "wrong-pass"), CancellationToken.None);

        // Assert
        result.Should().BeNull();
        bundle.Errs.IsValid.Should().BeFalse();
        bundle.Errs.GetFirstError().Should().Contain("用户名或密码错误");

        bundle.PasswordHasherMock.Verify(
            p => p.Verify("wrong-pass", "real-hash"),
            Times.Once,
            "用户存在时 Verify 必须执行一次（与用户不存在时次数一致才能对齐 timing）");
    }

    // ── Acceptance #97-4: 用户不存在与存在时 Verify 调用次数相等（timing oracle 关闭的核心指标） ──

    [Fact]
    public async Task GivenNonExistentAndExistingUser_WhenHandlingLogins_ThenVerifyCallCountIsEqual()
    {
        // Arrange — 用户不存在的场景
        var missingFactory = CreateFactory(Guid.NewGuid().ToString("N"));
        var missingPwMock = new Mock<IPasswordHasher>();
        missingPwMock.Setup(p => p.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(false);
        var missingBundle = CreateHandler(missingFactory, missingPwMock);

        // Arrange — 用户存在但密码错误的场景
        var presentDbName = Guid.NewGuid().ToString("N");
        var presentFactory = CreateFactory(presentDbName);
        await using (var ctx = presentFactory.CreateDbContext())
        {
            ctx.Users.Add(new TestUser
            {
                UserCode = "u001",
                DisplayName = "张三",
                PasswordHash = "real-hash",
                IsActive = true,
                UserType = UserType.Person,
            });
            await ctx.SaveChangesAsync();
        }

        var presentPwMock = new Mock<IPasswordHasher>();
        presentPwMock.Setup(p => p.Verify(It.IsAny<string>(), "real-hash")).Returns(false);
        var presentBundle = CreateHandler(presentFactory, presentPwMock);

        // Act
        _ = await missingBundle.Handler.HandleAsync(new LoginCommand("ghost", "x"), CancellationToken.None);
        _ = await presentBundle.Handler.HandleAsync(new LoginCommand("u001", "x"), CancellationToken.None);

        // Assert — 两次调用 Verify 的次数必须完全一致（都为 1）
        missingBundle.PasswordHasherMock.Verify(p => p.Verify(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        presentBundle.PasswordHasherMock.Verify(p => p.Verify(It.IsAny<string>(), "real-hash"), Times.Once);
    }

    // ── Acceptance #97-5: 即使用户不存在，Verify 也必须被调用（即 short-circuit `&&` 不被允许） ──

    [Fact]
    public async Task GivenNonExistentUser_WhenHandlingLogin_ThenVerifyIsInvoked_NotSkippedByShortCircuit()
    {
        // Arrange — DB 空；用 strict mock：若 Verify 没被调用，测试应失败
        var factory = CreateFactory(Guid.NewGuid().ToString("N"));
        var pwMock = new Mock<IPasswordHasher>(MockBehavior.Strict);
        pwMock.Setup(p => p.DummyHash).Returns("dummy-hash");
        pwMock.Setup(p => p.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(false);
        var bundle = CreateHandler(factory, pwMock);

        // Act
        _ = await bundle.Handler.HandleAsync(new LoginCommand(NonExistentUserCode, AnyPassword), CancellationToken.None);

        // Assert — strict mock 上没被调用的 Setup 会让 Verify 抛异常；
        // 显式 Times.Once 进一步锁定"必须执行一次"
        bundle.PasswordHasherMock.Verify(p => p.Verify(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }
}
