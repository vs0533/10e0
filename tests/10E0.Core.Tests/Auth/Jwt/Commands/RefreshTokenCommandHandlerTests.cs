using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TenE0.Core.Abstractions;
using TenE0.Core.Auditing;
using TenE0.Core.Auth.Jwt;
using TenE0.Core.Auth.Jwt.Commands;
using TenE0.Core.Auth.Jwt.Services;
using TenE0.Core.Auth.Jwt.Storage;
using TenE0.Core.Errors;
using TenE0.Core.Permissions.Storage;
using Microsoft.Extensions.Time.Testing;

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

    private static IOptions<JwtOptions> CreateJwtOptions(
        bool rotationEnabled = true,
        bool slidingEnabled = true,
        TimeSpan? refreshLifetime = null)
    {
        return Options.Create(new JwtOptions
        {
            Issuer = "test-issuer",
            Audience = "test-aud",
            SigningKey = "test-signing-key-32-bytes-minimum-1234",
            AccessTokenLifetime = TimeSpan.FromMinutes(30),
            RefreshTokenLifetime = refreshLifetime ?? TimeSpan.FromDays(14),
            RefreshTokenRotationEnabled = rotationEnabled,
            SlidingRefreshExpiration = slidingEnabled,
        });
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
        tokenMock.Setup(t => t.Issue("u001", "张三", UserType.Person, It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyDictionary<string, long>>()))
            .Returns(new IssuedTokens("newacc", expiresAt, "newref", "newhash", expiresAt.AddDays(7)));

        var errs = new Errs();
        var logger = NullLogger<RefreshTokenCommandHandler<TestUser, TestDbContext>>.Instance;
        var handler = new RefreshTokenCommandHandler<TestUser, TestDbContext>(factory, tokenMock.Object, timeProvider, CreateJwtOptions(), errs, logger, new NullAuditLogSink());

        var result = await handler.HandleAsync(new RefreshTokenCommand("valid-refresh"), CancellationToken.None);

        result.Should().NotBeNull();
        result.AccessToken.Should().Be("newacc");
        result.RefreshToken.Should().Be("newref");
        errs.IsValid.Should().BeTrue();

        await using var verifyCtx = factory.CreateDbContext();
        var oldToken = await verifyCtx.RefreshTokens.SingleAsync(t => t.TokenHash == "oldhash");
        oldToken.RevokedAt.Should().NotBeNull();
        oldToken.RevokedReason.Should().Be("rotated");
        oldToken.ReplacedByTokenHash.Should().Be("newhash");
        var newToken = await verifyCtx.RefreshTokens.SingleAsync(t => t.TokenHash == "newhash");
        newToken.RevokedAt.Should().BeNull();
    }

    /// <summary>
    /// #102 回归守护：合并 user + roles 为 left join 查询后，必须验证"用户无任何角色"
    /// 场景仍能正常 refresh —— left join 在无匹配 role 时应保留 user 行（RoleCode=null），
    /// 不能因无角色而查不到 user 导致 AuthDisabled 误报。
    /// </summary>
    [Fact]
    public async Task HandleAsync_ValidToken_UserWithNoRoles_StillRefreshesSuccessfully()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var now = DateTimeOffset.UtcNow;
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Users.Add(new TestUser { UserCode = "u-norole", DisplayName = "无角色用户", PasswordHash = "x", IsActive = true, UserType = UserType.Person });
            // 注意：不添加任何 TenE0UserRole 行
            ctx.RefreshTokens.Add(new TenE0RefreshToken { TokenHash = "hash-norole", UserCode = "u-norole", ExpiresAt = now.AddDays(7), CreatedByIp = "1.2.3.4" });
            await ctx.SaveChangesAsync();
        }

        var timeProvider = new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.Zero };
        timeProvider.SetUtcNow(now.AddHours(1));

        var tokenMock = new Mock<IJwtTokenService>();
        tokenMock.Setup(t => t.HashRefreshToken("ref-norole")).Returns("hash-norole");
        var expiresAt = now.AddHours(2);
        tokenMock.Setup(t => t.Issue("u-norole", "无角色用户", UserType.Person, It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyDictionary<string, long>>()))
            .Returns(new IssuedTokens("acc-norole", expiresAt, "ref-new", "hash-new", expiresAt.AddDays(7)));

        var errs = new Errs();
        var logger = NullLogger<RefreshTokenCommandHandler<TestUser, TestDbContext>>.Instance;
        var handler = new RefreshTokenCommandHandler<TestUser, TestDbContext>(factory, tokenMock.Object, timeProvider, CreateJwtOptions(), errs, logger, new NullAuditLogSink());

        var result = await handler.HandleAsync(new RefreshTokenCommand("ref-norole"), CancellationToken.None);

        result.Should().NotBeNull("无角色用户的 left join 应保留 user 行，不应误报账号不可用");
        result!.AccessToken.Should().Be("acc-norole");
        errs.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_TokenNotFound_ReturnsNullWithError()
    {
        var factory = CreateFactory(Guid.NewGuid().ToString("N"));
        var tokenMock = new Mock<IJwtTokenService>();
        tokenMock.Setup(t => t.HashRefreshToken("bad")).Returns("nohash");
        var errs = new Errs();
        var logger = NullLogger<RefreshTokenCommandHandler<TestUser, TestDbContext>>.Instance;
        var handler = new RefreshTokenCommandHandler<TestUser, TestDbContext>(factory, tokenMock.Object, TimeProvider.System, CreateJwtOptions(), errs, logger, new NullAuditLogSink());

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
        var handler = new RefreshTokenCommandHandler<TestUser, TestDbContext>(factory, tokenMock.Object, timeProvider, CreateJwtOptions(), errs, logger, new NullAuditLogSink());

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
        var handler = new RefreshTokenCommandHandler<TestUser, TestDbContext>(factory, tokenMock.Object, timeProvider, CreateJwtOptions(), errs, logger, new NullAuditLogSink());

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
        var handler = new RefreshTokenCommandHandler<TestUser, TestDbContext>(factory, tokenMock.Object, timeProvider, CreateJwtOptions(), errs, logger, new NullAuditLogSink());

        var result = await handler.HandleAsync(new RefreshTokenCommand("tok"), CancellationToken.None);

        result.Should().BeNull();
        errs.GetFirstError().Should().Contain("不可用");
    }

    #region Refresh Token Rotation + Sliding Expiration

    /// <summary>
    /// 成功刷新时：新 token 签发、旧 token 被撤销并标记 reason="rotated"、链追踪到新 token。
    /// </summary>
    [Fact]
    public async Task RefreshAsync_ValidToken_ReturnsNewTokensAndRevokesOld()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var now = DateTimeOffset.UtcNow;
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Users.Add(new TestUser { UserCode = "u001", DisplayName = "Alice", PasswordHash = "x", IsActive = true, UserType = UserType.Person });
            ctx.RefreshTokens.Add(new TenE0RefreshToken
            {
                TokenHash = "old-hash",
                UserCode = "u001",
                ExpiresAt = now.AddDays(2),
            });
            await ctx.SaveChangesAsync();
        }

        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now.AddDays(1));

        var tokenMock = new Mock<IJwtTokenService>();
        tokenMock.Setup(t => t.HashRefreshToken("old-token")).Returns("old-hash");
        tokenMock.Setup(t => t.Issue("u001", "Alice", UserType.Person, It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyDictionary<string, long>>()))
            .Returns(new IssuedTokens("new-acc", now.AddDays(1).AddHours(2), "new-ref", "new-hash", now.AddDays(1).AddDays(14)));

        var errs = new Errs();
        var logger = NullLogger<RefreshTokenCommandHandler<TestUser, TestDbContext>>.Instance;
        var handler = new RefreshTokenCommandHandler<TestUser, TestDbContext>(factory, tokenMock.Object, timeProvider, CreateJwtOptions(), errs, logger, new NullAuditLogSink());

        var result = await handler.HandleAsync(new RefreshTokenCommand("old-token"), CancellationToken.None);

        result.Should().NotBeNull();
        result.RefreshToken.Should().Be("new-ref");
        result.AccessToken.Should().Be("new-acc");
        errs.IsValid.Should().BeTrue();

        await using var verifyCtx = factory.CreateDbContext();
        var oldToken = await verifyCtx.RefreshTokens.SingleAsync(t => t.TokenHash == "old-hash");
        oldToken.RevokedAt.Should().Be(now.AddDays(1));
        oldToken.RevokedReason.Should().Be("rotated");
        oldToken.ReplacedByTokenHash.Should().Be("new-hash");

        var newToken = await verifyCtx.RefreshTokens.SingleAsync(t => t.TokenHash == "new-hash");
        newToken.RevokedAt.Should().BeNull();
        newToken.UserCode.Should().Be("u001");
    }

    /// <summary>
    /// 重放检测：用已撤销的 token 再次刷新 → 抛/返回错误，且该用户所有 active token 被撤销。
    /// </summary>
    [Fact]
    public async Task RefreshAsync_OldRevokedToken_ThrowsSecurityException()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var now = DateTimeOffset.UtcNow;
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Users.Add(new TestUser { UserCode = "u001", DisplayName = "X", PasswordHash = "x", IsActive = true, UserType = UserType.Person });
            ctx.RefreshTokens.Add(new TenE0RefreshToken
            {
                TokenHash = "old-revoked",
                UserCode = "u001",
                ExpiresAt = now.AddDays(7),
                RevokedAt = now.AddMinutes(-30),
            });
            ctx.RefreshTokens.Add(new TenE0RefreshToken { TokenHash = "active-1", UserCode = "u001", ExpiresAt = now.AddDays(7) });
            ctx.RefreshTokens.Add(new TenE0RefreshToken { TokenHash = "active-2", UserCode = "u001", ExpiresAt = now.AddDays(7) });
            await ctx.SaveChangesAsync();
        }

        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var tokenMock = new Mock<IJwtTokenService>();
        tokenMock.Setup(t => t.HashRefreshToken("replayed")).Returns("old-revoked");

        var errs = new Errs();
        var logger = NullLogger<RefreshTokenCommandHandler<TestUser, TestDbContext>>.Instance;
        var handler = new RefreshTokenCommandHandler<TestUser, TestDbContext>(factory, tokenMock.Object, timeProvider, CreateJwtOptions(), errs, logger, new NullAuditLogSink());

        var result = await handler.HandleAsync(new RefreshTokenCommand("replayed"), CancellationToken.None);

        // 复用了 codebase 现有的 IErrs 错误收集契约（已被 PermissionBehavior 转为 401/403）
        result.Should().BeNull();
        errs.IsValid.Should().BeFalse();
        errs.GetFirstError().Should().Contain("已撤销");
    }

    /// <summary>
    /// 重放检测：该用户所有 active token 都被撤销。
    /// </summary>
    [Fact]
    public async Task RefreshAsync_DetectsReuse_RevokesAllUserTokens()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var now = DateTimeOffset.UtcNow;
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Users.Add(new TestUser { UserCode = "u001", DisplayName = "X", PasswordHash = "x", IsActive = true, UserType = UserType.Person });
            // 三个 active + 一个已被撤销（重放对象）
            ctx.RefreshTokens.Add(new TenE0RefreshToken { TokenHash = "active-a", UserCode = "u001", ExpiresAt = now.AddDays(7) });
            ctx.RefreshTokens.Add(new TenE0RefreshToken { TokenHash = "active-b", UserCode = "u001", ExpiresAt = now.AddDays(7) });
            ctx.RefreshTokens.Add(new TenE0RefreshToken { TokenHash = "active-c", UserCode = "u001", ExpiresAt = now.AddDays(7) });
            ctx.RefreshTokens.Add(new TenE0RefreshToken
            {
                TokenHash = "revoked-target",
                UserCode = "u001",
                ExpiresAt = now.AddDays(7),
                RevokedAt = now.AddHours(-1),
                RevokedReason = "rotated",
            });
            await ctx.SaveChangesAsync();
        }

        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var tokenMock = new Mock<IJwtTokenService>();
        tokenMock.Setup(t => t.HashRefreshToken("replay-token")).Returns("revoked-target");

        var errs = new Errs();
        var logger = NullLogger<RefreshTokenCommandHandler<TestUser, TestDbContext>>.Instance;
        var handler = new RefreshTokenCommandHandler<TestUser, TestDbContext>(factory, tokenMock.Object, timeProvider, CreateJwtOptions(), errs, logger, new NullAuditLogSink());

        var result = await handler.HandleAsync(new RefreshTokenCommand("replay-token"), CancellationToken.None);

        result.Should().BeNull();

        await using var verifyCtx = factory.CreateDbContext();
        var allTokens = await verifyCtx.RefreshTokens.ToListAsync();
        allTokens.Should().AllSatisfy(t => t.RevokedAt.Should().NotBeNull("reuse detection must revoke all user tokens"));
        // 重放对象标记 reason（如果之前是 null）
        var replayed = await verifyCtx.RefreshTokens.SingleAsync(t => t.TokenHash == "revoked-target");
        replayed.RevokedReason.Should().Be("token_reuse_detected");
    }

    /// <summary>
    /// 滑动过期：新 refresh token 的过期时间 = now + RefreshTokenLifetime（不是原 token 的过期时间）。
    /// </summary>
    [Fact]
    public async Task RefreshAsync_SlidingExpiration_NewExpiryIsNowPlusLifetime()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var baseTime = new DateTimeOffset(2026, 6, 2, 12, 0, 0, TimeSpan.Zero);
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Users.Add(new TestUser { UserCode = "u001", DisplayName = "Alice", PasswordHash = "x", IsActive = true, UserType = UserType.Person });
            // 旧 token 原本还有 14 天到期（baseTime + 14d），将于 baseTime+7d 之后还能续 7 天
            ctx.RefreshTokens.Add(new TenE0RefreshToken
            {
                TokenHash = "old-hash",
                UserCode = "u001",
                ExpiresAt = baseTime.AddDays(14),
            });
            await ctx.SaveChangesAsync();
        }

        // 7 天后才刷新（token 仍然有效）
        var now = baseTime.AddDays(7);
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var lifetime = TimeSpan.FromDays(14);
        var tokenMock = new Mock<IJwtTokenService>();
        tokenMock.Setup(t => t.HashRefreshToken("valid")).Returns("old-hash");
        tokenMock.Setup(t => t.Issue("u001", "Alice", UserType.Person, It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyDictionary<string, long>>()))
            .Returns(new IssuedTokens("acc", now.AddHours(1), "ref", "new-hash", now.Add(lifetime)));

        var errs = new Errs();
        var logger = NullLogger<RefreshTokenCommandHandler<TestUser, TestDbContext>>.Instance;
        var handler = new RefreshTokenCommandHandler<TestUser, TestDbContext>(
            factory, tokenMock.Object, timeProvider,
            CreateJwtOptions(slidingEnabled: true, refreshLifetime: lifetime),
            errs, logger, new NullAuditLogSink());

        var result = await handler.HandleAsync(new RefreshTokenCommand("valid"), CancellationToken.None);

        result.Should().NotBeNull();
        // 关键断言：返回的 RefreshTokenExpiresAt 必须基于 now + lifetime（滑动），而不是原 token 的 expiresAt
        result.RefreshTokenExpiresAt.Should().BeCloseTo(now.Add(lifetime), TimeSpan.FromSeconds(1));
        result.RefreshTokenExpiresAt.Should().NotBeCloseTo(baseTime.AddDays(14), TimeSpan.FromHours(1));

        await using var verifyCtx = factory.CreateDbContext();
        var newToken = await verifyCtx.RefreshTokens.SingleAsync(t => t.TokenHash == "new-hash");
        newToken.ExpiresAt.Should().BeCloseTo(now.Add(lifetime), TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// 关闭滑动过期时：新 token 保持原过期时间（不向后扩展），避免无限延长。
    /// </summary>
    [Fact]
    public async Task RefreshAsync_SlidingDisabled_KeepsOriginalExpiry()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var baseTime = new DateTimeOffset(2026, 6, 2, 12, 0, 0, TimeSpan.Zero);
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Users.Add(new TestUser { UserCode = "u001", DisplayName = "Alice", PasswordHash = "x", IsActive = true, UserType = UserType.Person });
            ctx.RefreshTokens.Add(new TenE0RefreshToken
            {
                TokenHash = "old-hash",
                UserCode = "u001",
                ExpiresAt = baseTime.AddDays(2), // 原过期时间
            });
            await ctx.SaveChangesAsync();
        }

        // 1 天后刷新
        var now = baseTime.AddDays(1);
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var lifetime = TimeSpan.FromDays(14);
        var tokenMock = new Mock<IJwtTokenService>();
        tokenMock.Setup(t => t.HashRefreshToken("valid")).Returns("old-hash");
        tokenMock.Setup(t => t.Issue("u001", "Alice", UserType.Person, It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyDictionary<string, long>>()))
            .Returns(new IssuedTokens("acc", now.AddHours(1), "ref", "new-hash", now.Add(lifetime)));

        var errs = new Errs();
        var logger = NullLogger<RefreshTokenCommandHandler<TestUser, TestDbContext>>.Instance;
        var handler = new RefreshTokenCommandHandler<TestUser, TestDbContext>(
            factory, tokenMock.Object, timeProvider,
            CreateJwtOptions(slidingEnabled: false, refreshLifetime: lifetime),
            errs, logger, new NullAuditLogSink());

        var result = await handler.HandleAsync(new RefreshTokenCommand("valid"), CancellationToken.None);

        result.Should().NotBeNull();
        // 关闭滑动：保持原过期时间
        result.RefreshTokenExpiresAt.Should().BeCloseTo(baseTime.AddDays(2), TimeSpan.FromSeconds(1));

        await using var verifyCtx = factory.CreateDbContext();
        var newToken = await verifyCtx.RefreshTokens.SingleAsync(t => t.TokenHash == "new-hash");
        newToken.ExpiresAt.Should().BeCloseTo(baseTime.AddDays(2), TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// 关闭旋转时：旧 token 仍有效，新 token 也会被签发。
    /// </summary>
    [Fact]
    public async Task RefreshAsync_RotationDisabled_OldTokenRemainsValid()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var now = DateTimeOffset.UtcNow;
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Users.Add(new TestUser { UserCode = "u001", DisplayName = "Alice", PasswordHash = "x", IsActive = true, UserType = UserType.Person });
            ctx.RefreshTokens.Add(new TenE0RefreshToken
            {
                TokenHash = "old-hash",
                UserCode = "u001",
                ExpiresAt = now.AddDays(7),
            });
            await ctx.SaveChangesAsync();
        }

        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var tokenMock = new Mock<IJwtTokenService>();
        tokenMock.Setup(t => t.HashRefreshToken("valid")).Returns("old-hash");
        tokenMock.Setup(t => t.Issue("u001", "Alice", UserType.Person, It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyDictionary<string, long>>()))
            .Returns(new IssuedTokens("acc", now.AddHours(1), "ref", "new-hash", now.AddDays(14)));

        var errs = new Errs();
        var logger = NullLogger<RefreshTokenCommandHandler<TestUser, TestDbContext>>.Instance;
        var handler = new RefreshTokenCommandHandler<TestUser, TestDbContext>(
            factory, tokenMock.Object, timeProvider,
            CreateJwtOptions(rotationEnabled: false),
            errs, logger, new NullAuditLogSink());

        var result = await handler.HandleAsync(new RefreshTokenCommand("valid"), CancellationToken.None);

        result.Should().NotBeNull();
        errs.IsValid.Should().BeTrue();

        await using var verifyCtx = factory.CreateDbContext();
        var oldToken = await verifyCtx.RefreshTokens.SingleAsync(t => t.TokenHash == "old-hash");
        // rotation 关闭：旧 token 不被撤销
        oldToken.RevokedAt.Should().BeNull();
        oldToken.RevokedReason.Should().BeNull();
        // 但新 token 仍会写入
        var newToken = await verifyCtx.RefreshTokens.SingleAsync(t => t.TokenHash == "new-hash");
        newToken.RevokedAt.Should().BeNull();
    }

    #endregion
}
