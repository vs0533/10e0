using System.Collections.Concurrent;
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
using TenE0.Core.Tests.Events.Outbox;

namespace TenE0.Core.Tests.Auth.Jwt.Commands;

/// <summary>
/// 并发验收测试（来源：issue #94 [P1] RefreshTokenCommandHandler TOCTOU）。
/// <para>
/// 真实 SQL Server 才支持 <c>ExecuteUpdateAsync</c>（EF Core 9 直发 UPDATE），
/// InMemory provider 不实现。本测试用 <see cref="SqlServerContainerFixture"/> 共享 SQL Server 容器。
/// </para>
/// <para>
/// 场景：同一 refresh token 在极短时间窗口内被两个并发请求复用。
/// 修复前：两个请求都从 tracked record 读到 <c>RevokedAt is null</c>，
/// 都走正常 rotation 路径写新 token，reuse-detection 完全不触发。
/// 修复后：<c>ExecuteUpdateAsync</c> + <c>WHERE RevokedAt IS NULL</c> 原子化撤销，
/// 竞争失败的一方（rows=0）进入 reuse-detection 分支，正确撤销用户全链。
/// </para>
/// </summary>
[Trait("Category", "Acceptance")]
[Trait("Requires", "Docker")]
public sealed class RefreshTokenRotationConcurrencyAcceptanceTests
    : IClassFixture<SqlServerContainerFixture>
{
    private readonly SqlServerContainerFixture _fixture;

    public RefreshTokenRotationConcurrencyAcceptanceTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class TestUser : TenE0User { }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TestUser> Users => Set<TestUser>();
        public DbSet<TenE0UserRole> UserRoles => Set<TenE0UserRole>();
        public DbSet<TenE0RefreshToken> RefreshTokens => Set<TenE0RefreshToken>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestUser>(b =>
            {
                b.HasKey(e => e.Id);
                b.Property(u => u.UserCode);
            });
            modelBuilder.Entity<TenE0UserRole>(b =>
                b.HasKey(nameof(TenE0UserRole.UserCode), nameof(TenE0UserRole.RoleCode)));
            modelBuilder.Entity<TenE0RefreshToken>(b => b.HasKey(e => e.Id));
            modelBuilder.Entity<TenE0Role>(b => b.HasKey(r => r.Code));
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            optionsBuilder.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<TestDbContext> options)
        : IDbContextFactory<TestDbContext>
    {
        public TestDbContext CreateDbContext() => new(options);
    }

    private IDbContextFactory<TestDbContext> CreateFactory()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlServer(_fixture.ConnectionString)
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

    [Fact]
    public async Task GivenSameRefreshTokenRefreshedConcurrently_WhenTwoTasksRace_ThenExactlyOneSucceedsAndReuseDetectionFiresForLoser()
    {
        // 跳过：本地无 Docker 守护进程时 fixture 没起来
        if (string.IsNullOrEmpty(_fixture.ConnectionString))
        {
            Assert.Fail("Requires=Docker：本地未启动 Docker 守护进程，跳过本测试（SqlServerContainerFixture 未拉起 SQL Server 容器）");
            return;
        }

        // Arrange：fixture 共享容器 + 跨测试复用 schema（不能用 EnsureDeleted —
        // SQL Server master 库报 "Option 'SINGLE_USER' cannot be set in database 'master'"，
        // PR #94 CI 修复后改用 fixture.EnsureRefreshTokenSchemaAsync + DELETE 行清表）。
        var factory = CreateFactory();
        await _fixture.EnsureRefreshTokenSchemaAsync();
        await _fixture.TruncateRefreshTokenTestDataAsync();
        await using (var setup = factory.CreateDbContext())
        {
            setup.Users.Add(new TestUser
            {
                UserCode = "alice",
                DisplayName = "Alice",
                PasswordHash = "x",
                IsActive = true,
                UserType = UserType.Person,
            });
            setup.RefreshTokens.Add(new TenE0RefreshToken
            {
                TokenHash = "shared-hash",
                UserCode = "alice",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            });
            await setup.SaveChangesAsync();
        }

        // Act：两个 task 几乎同时调 Refresh，复用同一 opaque token
        var results = new ConcurrentBag<RefreshOutcome>();
        var barrier = new SemaphoreSlim(0, 2);

        var task1 = Task.Run(async () =>
        {
            var (handler, errs) = NewHandlerPair(factory);
            await barrier.WaitAsync();
            var r = await handler.HandleAsync(new RefreshTokenCommand("opaque-shared", null), default);
            results.Add(new RefreshOutcome(r, errs));
        });
        var task2 = Task.Run(async () =>
        {
            var (handler, errs) = NewHandlerPair(factory);
            await barrier.WaitAsync();
            var r = await handler.HandleAsync(new RefreshTokenCommand("opaque-shared", null), default);
            results.Add(new RefreshOutcome(r, errs));
        });
        barrier.Release(2);
        await Task.WhenAll(task1, task2);

        // Assert 1：恰好 1 个 success，1 个 failure
        var successes = results.Count(o => o.Result is not null);
        var failures = results.Count(o => o.Result is null);
        successes.Should().Be(1,
            "两个并发 refresh 复用同一 token，必须恰好 1 个 rotation success + 1 个 reuse-detection failure");
        failures.Should().Be(1);

        // Assert 2：失败方必须走 reuse-detection 路径（TokenRevoked），不是 token_invalid
        var failed = results.First(o => o.Result is null);
        failed.Errs.IsValid.Should().BeFalse();
        failed.Errs.GetFirstError().Should().NotBeNull();
        var failedCodes = string.Join(",", failed.Errs.Entries.Select(e => e.Code ?? "<null>"));
        failedCodes.Should().Contain(ErrorCodes.TokenRevoked,
            "竞争失败的一方必须走 reuse-detection 路径（issue #94 修复前会被错认为新请求通过）");

        // Assert 3：旧 token RevokedReason 不能被两个 'rotated' 并发覆盖为空
        await using var verify = factory.CreateDbContext();
        var oldToken = await verify.RefreshTokens.SingleAsync(t => t.TokenHash == "shared-hash");
        oldToken.RevokedAt.Should().NotBeNull("旧 token 必然被撤销");
        oldToken.RevokedReason.Should().NotBeNullOrEmpty(
            "RevokedReason 不可被并发 last-write-wins 覆盖为空 — ExecuteUpdateAsync 原子更新保护");
        oldToken.RevokedReason.Should().BeOneOf("rotated", "token_reuse_detected");

        // Assert 4：reuse-detection 应撤销该用户所有 active token（最多保留 1 条新签发）
        var activeTokens = await verify.RefreshTokens
            .Where(t => t.UserCode == "alice" && t.RevokedAt == null)
            .ToListAsync();
        activeTokens.Count.Should().BeLessThanOrEqualTo(1,
            "reuse-detection 应撤销用户全链 active token；最多保留 1 条 success 签发的新 token");
    }

    private static (RefreshTokenCommandHandler<TestUser, TestDbContext> Handler, Errs Errs) NewHandlerPair(
        IDbContextFactory<TestDbContext> factory)
    {
        var errs = new Errs();
        var handler = new RefreshTokenCommandHandler<TestUser, TestDbContext>(
            factory,
            new StubJwtTokenService(),
            TimeProvider.System,
            CreateJwtOptions(),
            errs,
            NullLogger<RefreshTokenCommandHandler<TestUser, TestDbContext>>.Instance,
            new TenE0.Core.Auditing.NullAuditLogSink());
        return (handler, errs);
    }

    private sealed record RefreshOutcome(AuthResult? Result, IErrs Errs);

    /// <summary>
    /// Stub JWT token service：<c>HashRefreshToken</c> 固定返回 "shared-hash"
    /// 让两个并发请求查到同一行；<c>Issue</c> 返回确定性 token + 唯一 hash 用于跟踪轮换。
    /// </summary>
    private sealed class StubJwtTokenService : IJwtTokenService
    {
        private static int _counter;
        public IssuedTokens Issue(string userCode, string displayName, UserType userType,
            IReadOnlyList<string> roles, IReadOnlyDictionary<string, long> roleVersions, string? tenantId = null)
        {
            var i = Interlocked.Increment(ref _counter);
            return new IssuedTokens(
                AccessToken: $"access-{i}",
                AccessTokenExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
                RefreshToken: $"refresh-{i}",
                RefreshTokenHash: $"new-hash-{i}",
                RefreshTokenExpiresAt: DateTimeOffset.UtcNow.AddDays(14));
        }

        public (string Token, string Hash, DateTimeOffset ExpiresAt) GenerateRefreshToken() =>
            ($"generated-refresh-{Guid.NewGuid():N}", $"generated-hash-{Guid.NewGuid():N}", DateTimeOffset.UtcNow.AddDays(14));

        public string HashRefreshToken(string refreshToken) => "shared-hash";
    }
}
