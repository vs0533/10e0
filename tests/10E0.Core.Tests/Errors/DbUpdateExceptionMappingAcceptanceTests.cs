using Microsoft.EntityFrameworkCore;
using TenE0.Core.Common;
using TenE0.Core.Errors;

namespace TenE0.Core.Tests.Errors;

/// <summary>
/// BDD acceptance tests for issue #51 — disambiguating <see cref="DbUpdateException"/>
/// in <see cref="DefaultApiErrorMapper"/>.
///
/// Background: PR #48 collapsed every <c>DbUpdateException</c> into a generic
/// 409 / <c>CONFLICT</c>. Clients cannot tell a unique-key violation
/// (user already exists) from a foreign-key violation (referenced row
/// missing) from an optimistic-concurrency failure. The fix must let
/// callers branch on a stable, per-constraint error code.
///
/// Per the issue, the mapper should inspect <c>DbUpdateException.InnerException</c>
/// and dispatch on the EF provider:
///
///   * <c>Microsoft.Data.SqlClient.SqlException</c>  → <c>.Number</c>
///     (2627 / 2601 unique, 547 FK, 1205 deadlock, etc.)
///   * <c>Npgsql.PostgresException</c>               → <c>.SqlState</c>
///     (<c>23505</c> unique, <c>23503</c> FK, etc.)
///   * <c>MySqlConnector.MySqlException</c>          → <c>.Number</c>
///     (1062 unique, 1452 FK, etc.)
///
/// The mapper is expected to emit a per-constraint error code plus the
/// entity / field from <c>DbUpdateException.Entries</c>. Because Core does
/// not reference any specific provider package (to stay DB-agnostic), the
/// mapper must detect the inner exception by its <em>type name</em> and
/// pull the error number out of the message — so these tests use stand-in
/// exceptions whose type name + message mirror the real provider types.
///
/// Each scenario is a pure unit test against <c>DefaultApiErrorMapper.Map</c>.
/// Until the fix lands, the disambiguating tests fail RED because the
/// current mapper hard-codes <c>"CONFLICT"</c>.
/// </summary>
[Trait("Category", "BDD")]
public sealed class DbUpdateExceptionMappingAcceptanceTests
{
    // ── Existing behavior: generic 409 / CONFLICT is preserved ─────
    // The pre-#51 contract must not regress: a DbUpdateException with
    // no identifiable inner exception still maps to 409, just with a
    // generic code that signals "we couldn't classify this".

    [Fact]
    public void GivenDbUpdateExceptionWithoutInnerException_WhenMapped_ThenReturns409WithGenericConflictCode()
    {
        // Arrange
        var mapper = CreateMapper();
        var ex = new DbUpdateException("UNIQUE KEY violation on column 'Name'");

        // Act
        var (status, body) = mapper.Map(ex);

        // Assert
        status.Should().Be(409,
            "DB update failures are resource conflicts → 409 Conflict");
        body.success.Should().BeFalse();
        body.errorCode.Should().Be("DB_CONSTRAINT",
            "when the inner exception is missing or unrecognised, the mapper must " +
            "fall back to a generic 'unknown DB constraint' code so the response " +
            "is still distinguishable from non-DB 409s (e.g. optimistic concurrency)");
    }

    // ── SQL Server: unique-key violation (#2627 / #2601) ────────────

    [Fact]
    public void GivenDbUpdateExceptionWrappingSqlServerUniqueViolation_WhenMapped_ThenReturns409WithUniqueConstraintCode()
    {
        // Arrange — SqlException proxy (Core is DB-agnostic so we
        // mimic the type by full name + Number-as-message prefix).
        var inner = new SqlExceptionLike(number: 2627,
            message: "Violation of UNIQUE KEY constraint 'UX_Users_UserCode'. " +
                     "Cannot insert duplicate key in object 'dbo.Users'.");
        var ex = new DbUpdateException(
            "An error occurred while updating the entries.", inner);

        // Act
        var (status, body) = mapper.Map(ex);

        // Assert
        status.Should().Be(409);
        body.errorCode.Should().Be("UNIQUE_CONSTRAINT",
            "a unique-key violation must surface with a dedicated code so clients " +
            "can render 'this value is already taken' instead of a generic conflict");
    }

    [Fact]
    public void GivenDbUpdateExceptionWrappingSqlServerUniqueViolation_WhenMapped_ThenEntityNameIsExposed()
    {
        // Arrange
        var inner = new SqlExceptionLike(number: 2601,
            message: "Cannot insert duplicate key row in object 'dbo.TenE0User' " +
                     "with unique index 'IX_TenE0User_Email'.");
        var ex = new DbUpdateException("dup key", inner);

        // Act
        var (_, body) = mapper.Map(ex);

        // Assert — clients need the entity name to render a precise message
        // ("Email already in use" vs "UserCode already in use") and to know
        // which form field to highlight.
        body.errorMessage.Should().Contain("TenE0User",
            "the entity name from the inner exception must reach the client so " +
            "the UI can attribute the conflict to the right form");
    }

    // ── SQL Server: foreign-key violation (#547) ───────────────────

    [Fact]
    public void GivenDbUpdateExceptionWrappingSqlServerForeignKeyViolation_WhenMapped_ThenReturns409WithForeignKeyCode()
    {
        // Arrange
        var inner = new SqlExceptionLike(number: 547,
            message: "The INSERT statement conflicted with the FOREIGN KEY " +
                     "constraint 'FK_Demo_TenE0Org_OrgId'. The conflict occurred " +
                     "in database 'tene0', table 'dbo.TenE0Org'.");
        var ex = new DbUpdateException("FK conflict", inner);

        // Act
        var (status, body) = mapper.Map(ex);

        // Assert
        status.Should().Be(409);
        body.errorCode.Should().Be("FOREIGN_KEY_CONSTRAINT",
            "FK violations need a distinct code so the client can show " +
            "'referenced record does not exist' rather than 'duplicate value'");
    }

    // ── SQL Server: deadlock (#1205) ──────────────────────────────

    [Fact]
    public void GivenDbUpdateExceptionWrappingSqlServerDeadlock_WhenMapped_ThenReturns409WithDeadlockCode()
    {
        // Arrange — deadlocks are retryable, the client can show a soft error
        var inner = new SqlExceptionLike(number: 1205,
            message: "Transaction (Process ID 73) was deadlocked");
        var ex = new DbUpdateException("deadlock victim", inner);

        // Act
        var (status, body) = mapper.Map(ex);

        // Assert
        status.Should().Be(409,
            "deadlocks surface as 409 so the client can implement retry-with-backoff");
        body.errorCode.Should().Be("DB_DEADLOCK",
            "a stable deadlock code lets the client distinguish transient " +
            "concurrency failures from semantic conflicts");
    }

    // ── Postgres: unique-key violation (SQLSTATE 23505) ────────────

    [Fact]
    public void GivenDbUpdateExceptionWrappingPostgresUniqueViolation_WhenMapped_ThenReturns409WithUniqueConstraintCode()
    {
        // Arrange
        var inner = new PostgresExceptionLike(sqlState: "23505",
            messageText: "duplicate key value violates unique constraint \"UX_TenE0Role_Code\"");
        var ex = new DbUpdateException("dup key", inner);

        // Act
        var (status, body) = mapper.Map(ex);

        // Assert
        status.Should().Be(409);
        body.errorCode.Should().Be("UNIQUE_CONSTRAINT",
            "Postgres 23505 must be classified as a unique-key violation just " +
            "like SQL Server 2627/2601 — providers agree on the business meaning");
    }

    // ── Postgres: foreign-key violation (SQLSTATE 23503) ───────────

    [Fact]
    public void GivenDbUpdateExceptionWrappingPostgresForeignKeyViolation_WhenMapped_ThenReturns409WithForeignKeyCode()
    {
        // Arrange
        var inner = new PostgresExceptionLike(sqlState: "23503",
            messageText: "insert or update on table \"Demo\" violates foreign key " +
                         "constraint \"FK_Demo_TenE0Org_OrgId\"");
        var ex = new DbUpdateException("FK conflict", inner);

        // Act
        var (status, body) = mapper.Map(ex);

        // Assert
        status.Should().Be(409);
        body.errorCode.Should().Be("FOREIGN_KEY_CONSTRAINT");
    }

    // ── MySQL: unique-key violation (#1062) ────────────────────────

    [Fact]
    public void GivenDbUpdateExceptionWrappingMySqlUniqueViolation_WhenMapped_ThenReturns409WithUniqueConstraintCode()
    {
        // Arrange
        var inner = new MySqlExceptionLike(number: 1062,
            messageText: "Duplicate entry 'admin' for key 'TenE0User.UX_TenE0User_UserCode'");
        var ex = new DbUpdateException("dup key", inner);

        // Act
        var (status, body) = mapper.Map(ex);

        // Assert
        status.Should().Be(409);
        body.errorCode.Should().Be("UNIQUE_CONSTRAINT",
            "MySQL 1062 must collapse into the same business-level code as " +
            "SQL Server 2627 and Postgres 23505 — providers are interchangeable");
    }

    // ── MySQL: foreign-key violation (#1452) ───────────────────────

    [Fact]
    public void GivenDbUpdateExceptionWrappingMySqlForeignKeyViolation_WhenMapped_ThenReturns409WithForeignKeyCode()
    {
        // Arrange
        var inner = new MySqlExceptionLike(number: 1452,
            messageText: "Cannot add or update a child row: a foreign key constraint fails " +
                         "(`tene0`.`Demo`, CONSTRAINT `FK_Demo_TenE0Org_OrgId`)");
        var ex = new DbUpdateException("FK conflict", inner);

        // Act
        var (status, body) = mapper.Map(ex);

        // Assert
        status.Should().Be(409);
        body.errorCode.Should().Be("FOREIGN_KEY_CONSTRAINT");
    }

    // ── Concurrency: DbUpdateConcurrencyException must be its own code ─

    [Fact]
    public void GivenDbUpdateConcurrencyException_WhenMapped_ThenReturns409WithConcurrencyConflictCode()
    {
        // Arrange — DbUpdateConcurrencyException is a *subclass* of
        // DbUpdateException, so the dispatch must check it BEFORE falling
        // through to the generic DB_CONSTRAINT branch. Otherwise optimistic-
        // locking failures would be misclassified as 'unknown DB error'.
        var ex = new DbUpdateConcurrencyException(
            "The database operation was expected to affect 1 row(s), but actually affected 0 row(s).");

        // Act
        var (status, body) = mapper.Map(ex);

        // Assert
        status.Should().Be(409);
        body.errorCode.Should().Be("CONCURRENCY_CONFLICT",
            "optimistic-concurrency failures need a dedicated code so the client " +
            "can prompt 'this record was changed by someone else, please reload'");
    }

    // ── Wire shape stays an ApiResult envelope on the disambiguated path ─

    [Fact]
    public void GivenDbUpdateExceptionWithClassifiableInner_WhenMapped_ThenBodyStillFollowsApiResultShape()
    {
        // Arrange — disambiguation must NOT bypass the ApiResult<T> envelope.
        // We assert on the same fields the success path uses so frontends can
        // deserialize with a single DTO.
        var inner = new SqlExceptionLike(number: 2627,
            message: "Violation of UNIQUE KEY constraint 'UX_Users_Email'.");
        var ex = new DbUpdateException("dup", inner);

        // Act
        var (_, body) = mapper.Map(ex);

        // Assert
        body.Should().NotBeNull(
            "every error response must be an ApiResult<object>, never a raw object");
        body.success.Should().BeFalse(
            "ApiResult<T> convention: success=false on every error body");
        body.errorCode.Should().NotBeNullOrEmpty(
            "errorCode is mandatory so clients can branch on it");
        body.errorMessage.Should().NotBeNullOrEmpty(
            "errorMessage is mandatory so the UI can render a human-readable reason");
    }

    // ── Unknown DB error numbers must NOT collapse to UNIQUE_CONSTRAINT ─

    [Fact]
    public void GivenDbUpdateExceptionWrappingUnrecognisedSqlError_WhenMapped_ThenReturns409WithDbConstraintCode()
    {
        // Arrange — error 8992 doesn't map to unique/FK/deadlock; we
        // deliberately pick a number far from the well-known ones so the
        // dispatch table can't accidentally match it.
        var inner = new SqlExceptionLike(number: 8992,
            message: "Some unknown SQL Server error");
        var ex = new DbUpdateException("unknown", inner);

        // Act
        var (status, body) = mapper.Map(ex);

        // Assert
        status.Should().Be(409);
        body.errorCode.Should().Be("DB_CONSTRAINT",
            "an unrecognised SQL error number must NOT be silently promoted to " +
            "UNIQUE_CONSTRAINT or FOREIGN_KEY_CONSTRAINT — the client would render " +
            "the wrong remediation. It must fall through to a generic code.");
    }

    // ── Helpers ────────────────────────────────────────────────

    private static readonly IApiErrorMapper mapper = new DefaultApiErrorMapper();
    private static IApiErrorMapper CreateMapper() => new DefaultApiErrorMapper();

    /// <summary>
    /// Stand-in for <c>Microsoft.Data.SqlClient.SqlException</c>. Core doesn't
    /// reference that package (DB-agnostic by design), so we mimic the
    /// contract the mapper actually needs: a stable <c>Type.FullName</c>
    /// plus the error number carried on the message. <c>ToString()</c> on
    /// the message is the dispatch key the mapper reads.
    /// </summary>
    private sealed class SqlExceptionLike : Exception
    {
        public int Number { get; }
        public SqlExceptionLike(int number, string message) : base(message)
        {
            Number = number;
        }
        public override string ToString() => $"SqlException (Number={Number}): {Message}";
    }

    /// <summary>
    /// Stand-in for <c>Npgsql.PostgresException</c>. Carries
    /// <c>SqlState</c> on the message so the mapper can read it without a
    /// hard reference to Npgsql.
    /// </summary>
    private sealed class PostgresExceptionLike : Exception
    {
        public string SqlState { get; }
        public PostgresExceptionLike(string sqlState, string messageText)
            : base($"{sqlState}: {messageText}")
        {
            SqlState = sqlState;
        }
    }

    /// <summary>
    /// Stand-in for <c>MySqlConnector.MySqlException</c>. Number travels
    /// on the message because Core stays package-agnostic.
    /// </summary>
    private sealed class MySqlExceptionLike : Exception
    {
        public int Number { get; }
        public MySqlExceptionLike(int number, string messageText)
            : base(BuildMessage(number, messageText))
        {
            Number = number;
        }
        private static string BuildMessage(int number, string messageText)
            => $"MySqlException (Number={number}): {messageText}";
    }
}
