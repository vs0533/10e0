using Microsoft.Extensions.Options;
using TenE0.Core.Abstractions;
using TenE0.Core.Permissions;

namespace TenE0.Core.Tests.Permissions;

/// <summary>
/// BDD-style acceptance tests for #7 "feat(auth): role version check for instant
/// permission revocation".
///
/// Each test name encodes a complete business scenario
/// (Given{State}_When{Action}_Then{Outcome}). These tests focus on observable
/// behavior: whether a user with a (possibly stale) access token can still
/// invoke a permission right after the underlying role grant/revoke.
///
/// They are written RED: the supporting types
/// (<see cref="IRoleVersionStore"/>, <see cref="ICurrentUserContext.RoleVersions"/>,
/// <see cref="JwtClaims.RoleVersion"/>) and the evaluator's version-check branch
/// do not exist yet, so this file fails to compile until the feature is
/// implemented.
/// </summary>
[Trait("Category", "BDD")]
public sealed class RoleVersionCheckAcceptanceTests
{
    // ── Shared test infrastructure ────────────────────────────

    private const string PermKey = "demo.update";

    private static Mock<ICurrentUserContext> CreateUser(
        bool isAuthenticated,
        IReadOnlyList<string>? roleIds = null,
        IReadOnlyDictionary<string, long>? roleVersions = null)
    {
        var mock = new Mock<ICurrentUserContext>();
        mock.Setup(u => u.IsAuthenticated).Returns(isAuthenticated);
        mock.Setup(u => u.RoleIds).Returns(roleIds ?? Array.Empty<string>());
        mock.Setup(u => u.RoleVersions).Returns(roleVersions ?? new Dictionary<string, long>());
        return mock;
    }

    private static Mock<IRoleVersionStore> CreateVersionStore(
        IReadOnlyDictionary<string, long>? currentVersions = null)
    {
        var mock = new Mock<IRoleVersionStore>();
        mock.Setup(s => s.GetCurrentVersionsAsync(
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, long>)
                (currentVersions ?? new Dictionary<string, long>()));
        return mock;
    }

    private static Mock<IPermissionCache> CreateCacheWithRolePerms(string role, params string[] keys)
    {
        var mock = new Mock<IPermissionCache>();
        mock.Setup(c => c.GetRolePermissionsAsync(role, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(keys, StringComparer.Ordinal));
        return mock;
    }

    private static IOptions<PermissionsOptions> CreateOptions() =>
        Options.Create(new PermissionsOptions());

    // ── Happy path: token version matches DB ──────────────────

    [Fact]
    public async Task GivenUserWithUpToDateRoleVersion_WhenCheckingPermission_ThenAllowed()
    {
        // Arrange
        var user = CreateUser(
            isAuthenticated: true,
            roleIds: new[] { "editor" },
            roleVersions: new Dictionary<string, long> { ["editor"] = 7L });
        var versionStore = CreateVersionStore(
            currentVersions: new Dictionary<string, long> { ["editor"] = 7L });
        var cache = CreateCacheWithRolePerms("editor", PermKey);
        var sut = new PermissionEvaluator(
            user.Object, Mock.Of<IPermissionStore>(), cache.Object,
            versionStore.Object, CreateOptions());

        // Act
        var result = await sut.HasAsync(PermKey);

        // Assert
        result.Should().BeTrue("token version matches DB version → permission is still valid");
    }

    // ── Core acceptance criterion #1: instant revocation ─────

    [Fact]
    public async Task GivenUserWithStaleRoleVersion_WhenRevokedInBackground_ThenNextHasAsyncReturnsFalse()
    {
        // Arrange — token claims role version 5, but DB is now at 6 (admin just revoked)
        var user = CreateUser(
            isAuthenticated: true,
            roleIds: new[] { "editor" },
            roleVersions: new Dictionary<string, long> { ["editor"] = 5L });
        var versionStore = CreateVersionStore(
            currentVersions: new Dictionary<string, long> { ["editor"] = 6L });
        var cache = CreateCacheWithRolePerms("editor", PermKey);
        var sut = new PermissionEvaluator(
            user.Object, Mock.Of<IPermissionStore>(), cache.Object,
            versionStore.Object, CreateOptions());

        // Act
        var result = await sut.HasAsync(PermKey);

        // Assert
        result.Should().BeFalse(
            "DB version (6) > token version (5) means admin revoked the permission after token was issued");
    }

    [Fact]
    public async Task GivenUserWithStaleRoleVersion_WhenRevokedInBackground_ThenStaleCacheEntryIsInvalidated()
    {
        // Arrange — token version 5, DB version 6 (revoked), cache has stale snapshot
        var user = CreateUser(
            isAuthenticated: true,
            roleIds: new[] { "editor" },
            roleVersions: new Dictionary<string, long> { ["editor"] = 5L });
        var versionStore = CreateVersionStore(
            currentVersions: new Dictionary<string, long> { ["editor"] = 6L });
        var cache = CreateCacheWithRolePerms("editor", PermKey);
        var sut = new PermissionEvaluator(
            user.Object, Mock.Of<IPermissionStore>(), cache.Object,
            versionStore.Object, CreateOptions());

        // Act
        await sut.HasAsync(PermKey);

        // Assert — must actively evict the stale role snapshot so the next caller
        // re-reads from the store. This is the "instant revocation" guarantee.
        cache.Verify(
            c => c.InvalidateRoleAsync("editor", It.IsAny<CancellationToken>()),
            Times.Once,
            "stale role-version mismatch must evict the cached role snapshot");
    }

    [Fact]
    public async Task GivenUserWithFreshRoleVersion_WhenHasAsyncInvoked_ThenCacheIsNotInvalidated()
    {
        // Arrange — version matches, cache should not be touched
        var user = CreateUser(
            isAuthenticated: true,
            roleIds: new[] { "editor" },
            roleVersions: new Dictionary<string, long> { ["editor"] = 9L });
        var versionStore = CreateVersionStore(
            currentVersions: new Dictionary<string, long> { ["editor"] = 9L });
        var cache = CreateCacheWithRolePerms("editor", PermKey);
        var sut = new PermissionEvaluator(
            user.Object, Mock.Of<IPermissionStore>(), cache.Object,
            versionStore.Object, CreateOptions());

        // Act
        await sut.HasAsync(PermKey);

        // Assert — no false eviction on the happy path
        cache.Verify(
            c => c.InvalidateRoleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "matching version means cache is trustworthy and must not be evicted");
    }

    // ── Core acceptance criterion: new grant is immediately visible ──

    [Fact]
    public async Task GivenUserTokenSnapshot_WhenGrantHappensThenVersionBumps_ThenNextHasAsyncSeesNewPermission()
    {
        // Arrange — simulate "token is one version old, admin just granted a new permission"
        // Cache still has the old snapshot; only version differs.
        var user = CreateUser(
            isAuthenticated: true,
            roleIds: new[] { "editor" },
            roleVersions: new Dictionary<string, long> { ["editor"] = 4L });
        var versionStore = CreateVersionStore(
            currentVersions: new Dictionary<string, long> { ["editor"] = 5L });
        // Old cache snapshot only has the previous grants
        var cache = CreateCacheWithRolePerms("editor", "demo.view");
        var store = new Mock<IPermissionStore>();
        // After eviction, the store will be re-queried and now includes the new perm
        store.Setup(s => s.GetGrantedPermissionsAsync(
                It.Is<IReadOnlyCollection<string>>(c => c.Contains("editor")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "demo.view", PermKey });

        var sut = new PermissionEvaluator(
            user.Object, store.Object, cache.Object,
            versionStore.Object, CreateOptions());

        // Act
        var result = await sut.HasAsync(PermKey);

        // Assert
        result.Should().BeTrue(
            "version bump after a new grant must trigger re-evaluation and the new permission is visible");
        cache.Verify(
            c => c.InvalidateRoleAsync("editor", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Edge: token has no version claim (legacy / pre-feature tokens) ──

    [Fact]
    public async Task GivenLegacyTokenWithoutRoleVersionClaim_WhenRoleUnchanged_ThenAllowFromCache()
    {
        // Arrange — legacy token has no RoleVersions claim (empty dictionary)
        var user = CreateUser(
            isAuthenticated: true,
            roleIds: new[] { "editor" },
            roleVersions: new Dictionary<string, long>());
        // DB version store still returns current version; the evaluator must
        // treat missing claim as "compatible" (don't deny legacy tokens on first deploy).
        var versionStore = CreateVersionStore(
            currentVersions: new Dictionary<string, long> { ["editor"] = 99L });
        var cache = CreateCacheWithRolePerms("editor", PermKey);
        var sut = new PermissionEvaluator(
            user.Object, Mock.Of<IPermissionStore>(), cache.Object,
            versionStore.Object, CreateOptions());

        // Act
        var result = await sut.HasAsync(PermKey);

        // Assert
        result.Should().BeTrue(
            "tokens issued before this feature shipped must continue to work; missing claim is not a deny signal");
    }

    // ── Edge: user holds multiple roles, any stale → deny (conservative) ──

    [Fact]
    public async Task GivenUserWithMultipleRolesAndOneStale_WhenAnyRoleVersionDrifts_ThenHasAsyncReturnsFalse()
    {
        // Arrange — user has editor(v5) + viewer(v8); DB now has editor(v6) + viewer(v8)
        var user = CreateUser(
            isAuthenticated: true,
            roleIds: new[] { "editor", "viewer" },
            roleVersions: new Dictionary<string, long>
            {
                ["editor"] = 5L,
                ["viewer"] = 8L,
            });
        var versionStore = CreateVersionStore(
            currentVersions: new Dictionary<string, long>
            {
                ["editor"] = 6L,
                ["viewer"] = 8L,
            });
        var cache = new Mock<IPermissionCache>();
        cache.Setup(c => c.GetRolePermissionsAsync("editor", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { PermKey });
        cache.Setup(c => c.GetRolePermissionsAsync("viewer", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "demo.view" });
        var sut = new PermissionEvaluator(
            user.Object, Mock.Of<IPermissionStore>(), cache.Object,
            versionStore.Object, CreateOptions());

        // Act
        var result = await sut.HasAsync(PermKey);

        // Assert — fail-closed: any single stale role invalidates the whole token's permission set
        result.Should().BeFalse(
            "any role-version drift is treated as 'permissions possibly changed' → fail closed");
    }

    // ── Edge: super user bypasses the version check ──

    [Fact]
    public async Task GivenSuperUserToken_WhenRoleVersionIsStale_ThenStillAllowed()
    {
        // Arrange — super_admin role, but token's recorded version is old
        var user = CreateUser(
            isAuthenticated: true,
            roleIds: new[] { "super_admin" },
            roleVersions: new Dictionary<string, long> { ["super_admin"] = 1L });
        var versionStore = CreateVersionStore(
            currentVersions: new Dictionary<string, long> { ["super_admin"] = 999L });
        var options = Options.Create(new PermissionsOptions
        {
            SuperUserRoles = new HashSet<string>(StringComparer.Ordinal) { "super_admin" },
        });
        var sut = new PermissionEvaluator(
            user.Object, Mock.Of<IPermissionStore>(),
            Mock.Of<IPermissionCache>(), versionStore.Object, options);

        // Act
        var result = await sut.HasAsync("any.permission");

        // Assert
        result.Should().BeTrue(
            "super-user short-circuit happens before the version check; the version store must not be consulted");
        versionStore.Verify(
            s => s.GetCurrentVersionsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Edge: unauthenticated user — version store must not be touched ──

    [Fact]
    public async Task GivenUnauthenticatedUser_WhenHasAsyncInvoked_ThenVersionStoreNotConsulted()
    {
        var user = CreateUser(isAuthenticated: false);
        var versionStore = new Mock<IRoleVersionStore>();
        var sut = new PermissionEvaluator(
            user.Object, Mock.Of<IPermissionStore>(), Mock.Of<IPermissionCache>(),
            versionStore.Object, CreateOptions());

        var result = await sut.HasAsync(PermKey);

        result.Should().BeFalse();
        versionStore.VerifyNoOtherCalls();
    }
}
