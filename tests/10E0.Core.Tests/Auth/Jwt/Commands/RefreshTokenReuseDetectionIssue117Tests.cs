using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TenE0.Core.Abstractions;
using TenE0.Core.Auth.Jwt;
using TenE0.Core.Auth.Jwt.Commands;
using TenE0.Core.Auth.Jwt.Services;
using TenE0.Core.Auth.Jwt.Storage;
using TenE0.Core.Errors;
using TenE0.Core.Permissions.Storage;
using Microsoft.Extensions.Time.Testing;

namespace TenE0.Core.Tests.Auth.Jwt.Commands;

/// <summary>
/// 单元测试：issue #117 [P2][Security] RefreshToken 重放检测不清 ReplacedByTokenHash。
///
/// <para>
/// 核心修复：在 <c>RefreshTokenCommandHandler.HandleAsync</c> 的 reuse-detection 分支
/// （<c>record.RevokedAt is not null</c>）清空 <c>record.ReplacedByTokenHash</c>，
/// 避免攻击者收到 401 后顺 hash 撞库（DB 已撤销新 token，但攻击信号没被运营系统识别）。
/// </para>
///
/// <para>
/// 本文件聚焦"核心 fix 行为"（单元粒度，不依赖多用户/全链撤回的复杂场景，那些由
/// <c>RefreshTokenReuseDetectionAcceptanceTests</c> 覆盖）。
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class RefreshTokenReuseDetectionIssue117Tests
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
            modelBuilder.Entity<TenE0UserRole>(b => { b.HasKey(nameof(TenE0UserRole.UserCode), nameof(TenE0UserRole.RoleCode)); });
            modelBuilder.Entity<TenE0RefreshToken>(b => b.HasKey(e => e.Id));
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

    private static IOptions<JwtOptions> CreateJwtOptions() => Options.Create(new JwtOptions
    {
        Issuer = "test-issuer",
        Audience = "test-aud",
        SigningKey = "test-signing-key-32-bytes-minimum-1234",
        AccessTokenLifetime = TimeSpan.FromMinutes(30),
        RefreshTokenLifetime = TimeSpan.FromDays(14),
        RefreshTokenRotationEnabled = true,
        SlidingRefreshExpiration = true,
    });

    /// <summary>
    /// 核心 fix（issue #117）：reuse-detection 路径必须把 <c>ReplacedByTokenHash</c> 清为 null。
    /// 即使攻击者拿到的 401 响应里没有 hash，DB 里残留的 hash 也会被审计/取证系统读出，
    /// 污染"token 链上下游"视图。
    /// </summary>
    [Fact]
    public async Task ReuseDetection_ClearsReplacedByTokenHash_OnReplayedToken()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var now = DateTimeOffset.UtcNow;
        const string replacedByHash = "rotated-to-this-hash";

        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Users.Add(new TestUser
            {
                UserCode = "u001",
                DisplayName = "X",
                PasswordHash = "x",
                IsActive = true,
                UserType = UserType.Person,
            });
            ctx.RefreshTokens.Add(new TenE0RefreshToken
            {
                TokenHash = "replayed-hash",
                UserCode = "u001",
                ExpiresAt = now.AddDays(7),
                RevokedAt = now.AddMinutes(-30),
                RevokedReason = "rotated",
                ReplacedByTokenHash = replacedByHash,
            });
            await ctx.SaveChangesAsync();
        }

        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var tokenMock = new Mock<IJwtTokenService>();
        tokenMock.Setup(t => t.HashRefreshToken("opaque-replay")).Returns("replayed-hash");

        var errs = new Errs();
        var handler = new RefreshTokenCommandHandler<TestUser, TestDbContext>(
            factory, tokenMock.Object, timeProvider, CreateJwtOptions(), errs,
            NullLogger<RefreshTokenCommandHandler<TestUser, TestDbContext>>.Instance,
            new TenE0.Core.Auditing.NullAuditLogSink());

        // Act
        var result = await handler.HandleAsync(new RefreshTokenCommand("opaque-replay"), CancellationToken.None);

        // Assert
        result.Should().BeNull("reuse detection 必须拒绝");
        errs.GetFirstError().Should().Contain("已撤销");

        await using var verifyCtx = factory.CreateDbContext();
        var replayed = await verifyCtx.RefreshTokens.SingleAsync(t => t.TokenHash == "replayed-hash");
        replayed.RevokedReason.Should().Be("token_reuse_detected");
        // 核心 fix 断言：ReplacedByTokenHash 必须被清空
        replayed.ReplacedByTokenHash.Should().BeNull(
            "issue #117: reuse detection 时必须清空 ReplacedByTokenHash，" +
            "避免攻击者顺 hash 撞库（DB 已撤销新 token 但攻击信号未被识别）");
    }

    /// <summary>
    /// 边界：连续两次重放同一已撤销 token，第二次触发 reuse-detection 时 <c>ReplacedByTokenHash</c>
    /// 仍应保持 null（幂等）。验证 fix 不会因为重复触发而写出脏数据。
    /// </summary>
    [Fact]
    public async Task ReuseDetection_RepeatedReplay_KeepsReplacedByTokenHashNull()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var now = DateTimeOffset.UtcNow;

        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Users.Add(new TestUser
            {
                UserCode = "u001",
                DisplayName = "X",
                PasswordHash = "x",
                IsActive = true,
                UserType = UserType.Person,
            });
            ctx.RefreshTokens.Add(new TenE0RefreshToken
            {
                TokenHash = "replayed-hash",
                UserCode = "u001",
                ExpiresAt = now.AddDays(7),
                RevokedAt = now.AddMinutes(-30),
                RevokedReason = "rotated",
                ReplacedByTokenHash = "some-hash",
            });
            await ctx.SaveChangesAsync();
        }

        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var tokenMock = new Mock<IJwtTokenService>();
        tokenMock.Setup(t => t.HashRefreshToken("opaque")).Returns("replayed-hash");

        var errs = new Errs();
        var handler = new RefreshTokenCommandHandler<TestUser, TestDbContext>(
            factory, tokenMock.Object, timeProvider, CreateJwtOptions(), errs,
            NullLogger<RefreshTokenCommandHandler<TestUser, TestDbContext>>.Instance,
            new TenE0.Core.Auditing.NullAuditLogSink());

        // Act：连续重放两次
        await handler.HandleAsync(new RefreshTokenCommand("opaque"), CancellationToken.None);
        errs.Clear();
        await handler.HandleAsync(new RefreshTokenCommand("opaque"), CancellationToken.None);

        // Assert
        await using var verifyCtx = factory.CreateDbContext();
        var replayed = await verifyCtx.RefreshTokens.SingleAsync(t => t.TokenHash == "replayed-hash");
        replayed.ReplacedByTokenHash.Should().BeNull(
            "连续重放后 ReplacedByTokenHash 仍应保持 null（幂等）");
        replayed.RevokedReason.Should().Be("token_reuse_detected");
    }

    /// <summary>
    /// Regression guard：正常 rotation 路径（active token 首次刷新）不能误伤
    /// <c>ReplacedByTokenHash</c> 串链行为。本测试确认 issue #117 的修改范围严格限定在
    /// "已 revoked token 被重放" 分支。
    /// </summary>
    [Fact]
    public async Task NormalRotation_KeepsReplacedByTokenHashLinkedToNewToken()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var now = DateTimeOffset.UtcNow;

        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Users.Add(new TestUser
            {
                UserCode = "u001",
                DisplayName = "Alice",
                PasswordHash = "x",
                IsActive = true,
                UserType = UserType.Person,
            });
            ctx.UserRoles.Add(new TenE0UserRole { UserCode = "u001", RoleCode = "user" });
            ctx.RefreshTokens.Add(new TenE0RefreshToken
            {
                TokenHash = "active-old",
                UserCode = "u001",
                ExpiresAt = now.AddDays(7),
            });
            await ctx.SaveChangesAsync();
        }

        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var tokenMock = new Mock<IJwtTokenService>();
        tokenMock.Setup(t => t.HashRefreshToken("old-opaque")).Returns("active-old");
        var newRefreshExpires = now.AddDays(14);
        tokenMock.Setup(t => t.Issue(
                "u001",
                "Alice",
                UserType.Person,
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyDictionary<string, long>>()))
            .Returns(new IssuedTokens("new-acc", now.AddHours(1), "new-ref", "new-hash", newRefreshExpires));

        var errs = new Errs();
        var handler = new RefreshTokenCommandHandler<TestUser, TestDbContext>(
            factory, tokenMock.Object, timeProvider, CreateJwtOptions(), errs,
            NullLogger<RefreshTokenCommandHandler<TestUser, TestDbContext>>.Instance,
            new TenE0.Core.Auditing.NullAuditLogSink());

        // Act
        var result = await handler.HandleAsync(new RefreshTokenCommand("old-opaque"), CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        await using var verifyCtx = factory.CreateDbContext();
        var oldToken = await verifyCtx.RefreshTokens.SingleAsync(t => t.TokenHash == "active-old");
        oldToken.RevokedReason.Should().Be("rotated");
        oldToken.ReplacedByTokenHash.Should().Be("new-hash",
            "正常 rotation 必须保留 ReplacedByTokenHash 串链，" +
            "这是 PR #6 rotation 链追踪的核心契约，issue #117 修复不能误伤");
    }
}
