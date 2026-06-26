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
/// Issue #117 验收测试 [P2][Security] RefreshToken 重放检测不清 ReplacedByTokenHash。
///
/// <para>
/// 业务背景：<see cref="RefreshTokenCommandHandler"/> 在检测到已 revoked token 被重放时
/// （<c>record.RevokedAt is not null</c> 分支），应同时清理
/// <see cref="TenE0RefreshToken.ReplacedByTokenHash"/> 字段。理由：
/// </para>
///
/// <list type="number">
///   <item>重放者收到 401 后可能顺 <c>ReplacedByTokenHash</c> 撞库新 token（DB 已撤销，但
///         攻击信号没被运营系统识别）。</item>
///   <item>重放场景下 <c>ReplacedByTokenHash</c> 指向的是"被强制下线的新 token"，继续暴露
///         该 hash 给攻击者等于泄漏链路拓扑。</item>
///   <item>审计/取证系统依赖 <c>ReplacedByTokenHash</c> 字段标识"token 链上下游"，重放事件
///         不应再指向一个本应被强制作废的 token。</item>
/// </list>
///
/// <para>
/// 正常 rotation 流程（<c>RevokedAt is null</c> → 撤销 + 写新 + 串链）<c>ReplacedByTokenHash</c>
/// 必须保留——这是 PR #6 rotation 链追踪的核心。本测试只覆盖"重放检测"分支
/// （已 revoked 的 token 再次使用）。
/// </para>
/// </summary>
[Trait("Category", "Acceptance")]
public sealed class RefreshTokenReuseDetectionAcceptanceTests
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

    private static IErrs CreateErrs() => new Errs();

    private static RefreshTokenCommandHandler<TestUser, TestDbContext> NewHandler(
        IDbContextFactory<TestDbContext> factory,
        Mock<IJwtTokenService> tokenMock,
        TimeProvider timeProvider,
        IErrs errs) =>
        new(factory, tokenMock.Object, timeProvider, CreateJwtOptions(), errs,
            NullLogger<RefreshTokenCommandHandler<TestUser, TestDbContext>>.Instance,
            new TenE0.Core.Auditing.NullAuditLogSink());

    /// <summary>
    /// 给定一个已 revoked 的 refresh token（其 <c>ReplacedByTokenHash</c> 指向某新 token），
    /// 当攻击者拿它再次调 refresh 时（重放），
    /// 那么该 token 的 <c>ReplacedByTokenHash</c> 必须被置为 null，
    /// 且 <c>RevokedReason</c> 被覆盖为 "token_reuse_detected"。
    /// </summary>
    /// <remarks>
    /// 修复前（issue #117）：重放检测路径只覆盖 <c>RevokedReason</c>，<c>ReplacedByTokenHash</c> 残留，
    /// 攻击者拿到 401 后可顺该 hash 撞库（虽然 DB 已撤销新 token，但攻击信号没被运营系统识别）。
    /// </remarks>
    [Fact]
    public async Task GivenRevokedTokenWithReplacedByHash_WhenReplayed_ThenReplacedByTokenHashCleared()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var now = DateTimeOffset.UtcNow;
        const string replacedByHash = "victim-new-hash-after-rotation";

        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Users.Add(new TestUser
            {
                UserCode = "u001",
                DisplayName = "张三",
                PasswordHash = "x",
                IsActive = true,
                UserType = UserType.Person,
            });
            // 已轮换撤销的旧 token：RevokedReason="rotated" + ReplacedByTokenHash=新 token hash
            ctx.RefreshTokens.Add(new TenE0RefreshToken
            {
                TokenHash = "old-hash",
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
        tokenMock.Setup(t => t.HashRefreshToken("replayed-opaque")).Returns("old-hash");

        var errs = CreateErrs();
        var handler = NewHandler(factory, tokenMock, timeProvider, errs);

        // Act
        var result = await handler.HandleAsync(new RefreshTokenCommand("replayed-opaque"), CancellationToken.None);

        // Assert
        result.Should().BeNull("重放已撤销 token 必须拒绝");
        errs.GetFirstError().Should().Contain("已撤销");

        await using var verifyCtx = factory.CreateDbContext();
        var replayedToken = await verifyCtx.RefreshTokens.SingleAsync(t => t.TokenHash == "old-hash");

        // 业务行为 1：重放事件必须覆盖原 reason
        replayedToken.RevokedReason.Should().Be("token_reuse_detected",
            "重放事件比轮换事件更严重，reason 应被覆盖为 token_reuse_detected");

        // 业务行为 2（issue #117 核心修复）：ReplacedByTokenHash 必须清空
        replayedToken.ReplacedByTokenHash.Should().BeNull(
            "重放检测后必须清空 ReplacedByTokenHash，避免攻击者顺该 hash 撞库新 token（DB 已撤销新 token 但攻击信号没被运营系统识别）");
    }

    /// <summary>
    /// 给定一个已 revoked 的 token（无 <c>ReplacedByTokenHash</c>）被重放，
    /// 当 reuse detection 触发时，
    /// 那么 <c>ReplacedByTokenHash</c> 仍应保持 null（不写入脏数据），且 reason 被覆盖。
    /// </summary>
    [Fact]
    public async Task GivenRevokedTokenWithoutReplacedByHash_WhenReplayed_ThenReplacedByTokenHashStaysNull()
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
            // 已撤销但 ReplacedByTokenHash 本身就是 null（e.g. 主动登出后被重放）
            ctx.RefreshTokens.Add(new TenE0RefreshToken
            {
                TokenHash = "logout-then-replay",
                UserCode = "u001",
                ExpiresAt = now.AddDays(7),
                RevokedAt = now.AddHours(-1),
                RevokedReason = "logout",
            });
            await ctx.SaveChangesAsync();
        }

        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var tokenMock = new Mock<IJwtTokenService>();
        tokenMock.Setup(t => t.HashRefreshToken("replayed")).Returns("logout-then-replay");

        var errs = CreateErrs();
        var handler = NewHandler(factory, tokenMock, timeProvider, errs);

        // Act
        var result = await handler.HandleAsync(new RefreshTokenCommand("replayed"), CancellationToken.None);

        // Assert
        result.Should().BeNull();
        await using var verifyCtx = factory.CreateDbContext();
        var replayedToken = await verifyCtx.RefreshTokens.SingleAsync(t => t.TokenHash == "logout-then-replay");
        replayedToken.ReplacedByTokenHash.Should().BeNull();
        replayedToken.RevokedReason.Should().Be("token_reuse_detected");
    }

    /// <summary>
    /// 给定一个 active（未撤销）的 refresh token 走正常 rotation 路径，
    /// 当 refresh 成功时，
    /// 那么旧 token 的 <c>ReplacedByTokenHash</c> 必须保留（指向新 token）——这是 PR #6 rotation 链追踪的核心。
    /// </summary>
    /// <remarks>
    /// 反向断言（regression guard）：修复 issue #117 时不能误伤正常 rotation 路径的链写入。
    /// </remarks>
    [Fact]
    public async Task GivenActiveToken_WhenRefreshed_ThenReplacedByTokenHashPointsToNewToken()
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
        tokenMock.Setup(t => t.HashRefreshToken("opaque-old")).Returns("active-old");
        var newRefreshExpires = now.AddDays(14);
        tokenMock.Setup(t => t.Issue(
                "u001",
                "Alice",
                UserType.Person,
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyDictionary<string, long>>()))
            .Returns(new IssuedTokens("new-acc", now.AddHours(1), "new-ref", "new-rotated-hash", newRefreshExpires));

        var errs = CreateErrs();
        var handler = NewHandler(factory, tokenMock, timeProvider, errs);

        // Act
        var result = await handler.HandleAsync(new RefreshTokenCommand("opaque-old"), CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        await using var verifyCtx = factory.CreateDbContext();
        var oldToken = await verifyCtx.RefreshTokens.SingleAsync(t => t.TokenHash == "active-old");
        oldToken.RevokedReason.Should().Be("rotated");
        oldToken.ReplacedByTokenHash.Should().Be("new-rotated-hash",
            "正常 rotation 路径必须保留 ReplacedByTokenHash 串成链，便于审计/重放检测；这是 PR #6 行为，issue #117 修复不能误伤");
    }

    /// <summary>
    /// 给定一个用户存在 active + revoked 两条 token，revoked 那条带 <c>ReplacedByTokenHash</c>，
    /// 当用 revoked 那条重放时触发 reuse detection，
    /// 那么：active 那条被撤销（reuse detection 标准行为），且 revoked 那条的 <c>ReplacedByTokenHash</c> 被清空。
    /// </summary>
    /// <remarks>
    /// 端到端断言：确保 issue #117 修复不会破坏 reuse detection 的核心契约（撤销用户全链），
    /// 同时新加的"清空 ReplacedByTokenHash"行为在该路径下也成立。
    /// </remarks>
    [Fact]
    public async Task GivenUserWithActiveAndRevokedTokens_WhenRevokedOneReplayed_ThenActiveRevokedAndReplacedByHashCleared()
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
            // active：新签发的 token
            ctx.RefreshTokens.Add(new TenE0RefreshToken
            {
                TokenHash = "user-active",
                UserCode = "u001",
                ExpiresAt = now.AddDays(7),
            });
            // revoked：replaced-by 指向 user-active（攻击者拿到 hash 后会撞这个）
            ctx.RefreshTokens.Add(new TenE0RefreshToken
            {
                TokenHash = "user-revoked",
                UserCode = "u001",
                ExpiresAt = now.AddDays(7),
                RevokedAt = now.AddHours(-2),
                RevokedReason = "rotated",
                ReplacedByTokenHash = "user-active",
            });
            await ctx.SaveChangesAsync();
        }

        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var tokenMock = new Mock<IJwtTokenService>();
        tokenMock.Setup(t => t.HashRefreshToken("replay-opaque")).Returns("user-revoked");

        var errs = CreateErrs();
        var handler = NewHandler(factory, tokenMock, timeProvider, errs);

        // Act
        var result = await handler.HandleAsync(new RefreshTokenCommand("replay-opaque"), CancellationToken.None);

        // Assert
        result.Should().BeNull("重放必须拒绝");
        errs.GetFirstError().Should().Contain("已撤销");

        await using var verifyCtx = factory.CreateDbContext();
        var allTokens = await verifyCtx.RefreshTokens.OrderBy(t => t.TokenHash).ToListAsync();

        // 业务契约 1：reuse detection 撤销用户全链（包括原本 active 的 user-active）
        allTokens.Should().AllSatisfy(t => t.RevokedAt.Should().NotBeNull(
            "reuse detection 必须撤销该用户所有 active token（PR #6 + OWASP）"));

        // 业务契约 2（issue #117）：被重放那条的 ReplacedByTokenHash 必须清空
        var replayedToken = allTokens.Single(t => t.TokenHash == "user-revoked");
        replayedToken.ReplacedByTokenHash.Should().BeNull(
            "issue #117 修复：重放检测时必须清空 ReplacedByTokenHash，避免攻击者顺 hash 撞 user-active（DB 已撤销但攻击信号未标记）");
        replayedToken.RevokedReason.Should().Be("token_reuse_detected");
    }
}
