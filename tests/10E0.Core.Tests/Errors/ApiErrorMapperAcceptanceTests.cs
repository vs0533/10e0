using TenE0.Core.Errors;
using TenE0.Core.Permissions.Behaviors;

namespace TenE0.Core.Tests.Errors;

/// <summary>
/// BDD acceptance tests for issue #39 — centralized exception mapping.
///
/// Goal: a single <c>IApiErrorMapper</c> turns every domain/framework exception
/// into a deterministic (HTTP status, ApiResult&lt;object&gt;) tuple. These tests
/// pin down the 5 mapping rules called out in the issue:
///
///   1. <see cref="PermissionDeniedException"/>  → 403 / PERM_DENIED
///   2. <see cref="ArgumentException"/>         → 400 / VALIDATION_ERROR
///   3. <see cref="InvalidOperationException"/>  → 400 / INVALID_OPERATION
///   4. <see cref="DbUpdateException"/> (no classifiable inner) → 409 / DB_CONSTRAINT  (#51 supersedes the pre-#51 CONFLICT)
///   5. any other exception                    → 500 / INTERNAL_ERROR (no stack leak)
///
/// Each scenario encodes the Given/When/Then business behavior; the mapper
/// implementation is expected to make them GREEN. Until the mapper lands,
/// these tests will fail to compile / fail at runtime — exactly the RED
/// state the issue asks for.
/// </summary>
[Trait("Category", "BDD")]
public sealed class ApiErrorMapperAcceptanceTests
{
    // ── PermissionDeniedException ──────────────────────────────

    [Fact]
    public void GivenPermissionDeniedException_WhenMapped_ThenReturns403WithPermDeniedCode()
    {
        // Arrange
        var mapper = CreateMapper();
        var ex = new PermissionDeniedException(
            commandName: "CreateDemoCommand",
            requiredKeys: new[] { "demo.create" });

        // Act
        var (status, body) = mapper.Map(ex);

        // Assert
        status.Should().Be(403,
            "permission failures must surface as HTTP 403 Forbidden");
        body.Should().NotBeNull();
        body.success.Should().BeFalse(
            "the mapped payload is an ApiResult failure shape");
        body.errorCode.Should().Be("PERM_DENIED",
            "front-end clients key off this code to render the 403 banner");
        body.errorMessage.Should().Be(ex.Message,
            "the human-readable reason must be preserved verbatim");
    }

    // ── AccountLockedException (#162) ─────────────────────────

    [Fact]
    public void GivenAccountLockedException_WhenMapped_ThenReturns423WithAuthLockedCode()
    {
        // Arrange
        var mapper = CreateMapper();
        var lockedUntil = DateTimeOffset.Parse("2026-01-01T00:15:00Z");
        var ex = new TenE0.Core.Security.LoginProtection.AccountLockedException("u001", lockedUntil);

        // Act
        var (status, body) = mapper.Map(ex);

        // Assert
        status.Should().Be(423,
            "账号锁定对应 HTTP 423 Locked（#162）");
        body.errorCode.Should().Be("AUTH_LOCKED");
        body.success.Should().BeFalse();
        body.errorMessage.Should().Contain("锁定");
    }

    // ── Validation (ArgumentException) ─────────────────────────

    [Fact]
    public void GivenArgumentException_WhenMapped_ThenReturns400WithValidationErrorCode()
    {
        // Arrange
        var mapper = CreateMapper();
        var ex = new ArgumentException("name must not be empty", "name");

        // Act
        var (status, body) = mapper.Map(ex);

        // Assert
        status.Should().Be(400,
            "argument validation failures are caller errors → 400 Bad Request");
        body.errorCode.Should().Be("VALIDATION_ERROR");
        body.errorMessage.Should().Be("name must not be empty");
        body.success.Should().BeFalse();
    }

    // ── InvalidOperationException ──────────────────────────────

    [Fact]
    public void GivenInvalidOperationException_WhenMapped_ThenReturns400WithInvalidOperationCode()
    {
        // Arrange
        var mapper = CreateMapper();
        var ex = new InvalidOperationException("Demo 已发布，不可重复发布");

        // Act
        var (status, body) = mapper.Map(ex);

        // Assert
        status.Should().Be(400,
            "domain rule violations are caller errors → 400 Bad Request");
        body.errorCode.Should().Be("INVALID_OPERATION");
        body.errorMessage.Should().Be("Demo 已发布，不可重复发布");
        body.success.Should().BeFalse();
    }

    // ── DbUpdateException (no classifiable inner) ──────────────
    // #51 supersedes the pre-#51 "CONFLICT" code: a plain
    // DbUpdateException with no recognised provider inner now
    // surfaces as DB_CONSTRAINT so clients can still tell it apart
    // from a unique-key / FK / concurrency-specific 409. Disambiguated
    // scenarios (unique / FK / deadlock / concurrency) live in
    // DbUpdateExceptionMappingAcceptanceTests.

    [Fact]
    public void GivenDbUpdateException_WhenMapped_ThenReturns409WithDbConstraintCode()
    {
        // Arrange
        var mapper = CreateMapper();
        var ex = new DbUpdateException("UNIQUE KEY violation on column 'Name'");

        // Act
        var (status, body) = mapper.Map(ex);

        // Assert
        status.Should().Be(409,
            "DB update failures are resource conflicts → 409 Conflict");
        body.errorCode.Should().Be("DB_CONSTRAINT",
            "with no classifiable inner the mapper must fall back to a " +
            "generic 'unknown DB constraint' code, NOT the legacy 'CONFLICT' " +
            "code that pre-#51 used for every DbUpdateException");
        body.success.Should().BeFalse();
    }

    // ── Fallback: any other exception → 500 (no stack leak) ───

    [Fact]
    public void GivenUnexpectedException_WhenMapped_ThenReturns500AndDoesNotLeakStackTrace()
    {
        // Arrange
        var mapper = CreateMapper();
        // The message intentionally includes details that should NEVER reach clients
        // (file path, connection string fragment, internal class name).
        var ex = new Exception(
            "Secret: at /var/secrets/db.txt line 42, query 'SELECT * FROM users' failed");

        // Act
        var (status, body) = mapper.Map(ex);

        // Assert
        status.Should().Be(500,
            "anything not explicitly mapped must surface as 500 Internal Server Error");
        body.errorCode.Should().Be("INTERNAL_ERROR");
        body.success.Should().BeFalse();
        body.errorMessage.Should().Be("Internal server error",
            "the client must see a stable, non-leaking message regardless of inner detail");
        body.errorMessage.Should().NotContain("/var/secrets",
            "raw exception messages must not leak internal paths");
        body.errorMessage.Should().NotContain("SELECT * FROM users",
            "raw exception messages must not leak SQL fragments");
    }

    [Fact]
    public void GivenUnexpectedException_WhenMapped_ThenApiResultShapeIsStable()
    {
        // Arrange — pin the wire shape so frontends can rely on it
        var mapper = CreateMapper();
        var ex = new FormatException("any unknown inner failure");

        // Act
        var (_, body) = mapper.Map(ex);

        // Assert — every ApiResult field must be present and well-typed
        body.Should().NotBeNull();
        body.success.Should().BeFalse();
        body.errorCode.Should().NotBeNullOrEmpty(
            "errorCode is required so clients can branch on it");
        body.errorMessage.Should().NotBeNullOrEmpty(
            "errorMessage is required so clients can render to the user");
    }

    // ── Null guard ─────────────────────────────────────────────

    [Fact]
    public void GivenNullException_WhenMapping_ThenArgumentNullExceptionIsThrown()
    {
        // Arrange
        var mapper = CreateMapper();

        // Act
        var act = () => mapper.Map(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>(
            "the mapper must refuse null inputs at the system boundary");
    }

    // ── Helpers ────────────────────────────────────────────────

    /// <summary>
    /// Constructs the production mapper under test.
    /// </summary>
    private static IApiErrorMapper CreateMapper() => new DefaultApiErrorMapper();
}
