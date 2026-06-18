using Microsoft.EntityFrameworkCore;

namespace TenE0.Core.Errors;

/// <summary>
/// Classifies a <see cref="DbUpdateException"/> into a stable, provider-agnostic
/// category that the centralized <see cref="IApiErrorMapper"/> can branch on.
///
/// Introduced for issue #51 (followup to #48). PR #48 collapsed every
/// <c>DbUpdateException</c> into a generic 409 / <c>CONFLICT</c>, which made
/// it impossible for clients to tell a unique-key violation from a
/// foreign-key violation from a deadlock or an optimistic-concurrency
/// failure. The classifier is the seam that lets a provider-specific
/// implementation (SQL Server, Postgres, MySQL) plug in without forcing
/// <c>TenE0.Core</c> to take a hard reference to any provider package.
///
/// Implementations MUST be:
/// <list type="bullet">
///   <item>Deterministic and side-effect-free (the mapper is a singleton).</item>
///   <item>Safe to call on any exception — never throw for an unrecognised
///         inner; just return <see cref="DbErrorKind.Other"/>.</item>
///   <item>Provider-agnostic at the contract level: the
///         <see cref="DbErrorClassification"/> they return does not leak
///         provider-specific types or numbers to the caller.</item>
/// </list>
/// </summary>
public interface IDbErrorClassifier
{
    /// <summary>
    /// Inspect <paramref name="exception"/> (and any provider-specific
    /// <c>InnerException</c>) and return a stable classification.
    /// </summary>
    /// <param name="exception">
    /// The <see cref="DbUpdateException"/> raised by EF Core / the provider.
    /// Must not be null — the mapper enforces that contract upstream.
    /// </param>
    /// <returns>
    /// A <see cref="DbErrorClassification"/> describing the constraint
    /// family plus whatever entity / field could be safely extracted from
    /// the inner exception's message. Never null.
    /// </returns>
    DbErrorClassification Classify(DbUpdateException exception);
}

/// <summary>
/// Canonical, provider-agnostic constraint family. New values must be
/// added at the end to preserve wire-compatibility of the
/// <c>errorCode</c> strings the mapper emits.
/// </summary>
public enum DbErrorKind
{
    /// <summary>
    /// Classifier could not identify the inner exception. The mapper
    /// surfaces this as 409 / <c>DB_CONSTRAINT</c>.
    /// </summary>
    Other = 0,

    /// <summary>
    /// Unique-key / duplicate-value violation (SQL Server 2627/2601,
    /// Postgres 23505, MySQL 1062). Wire code: <c>UNIQUE_CONSTRAINT</c>.
    /// </summary>
    UniqueKey = 1,

    /// <summary>
    /// Foreign-key violation — referenced row does not exist
    /// (SQL Server 547, Postgres 23503, MySQL 1452). Wire code:
    /// <c>FOREIGN_KEY_CONSTRAINT</c>.
    /// </summary>
    ForeignKey = 2,

    /// <summary>
    /// Optimistic-concurrency failure. Detected by the
    /// <see cref="DbUpdateConcurrencyException"/> subclass check
    /// (no provider-specific number needed). Wire code:
    /// <c>CONCURRENCY_CONFLICT</c>.
    /// </summary>
    Concurrency = 3,

    /// <summary>
    /// Database deadlock victim. Treat as transient / retryable.
    /// Wire code: <c>DB_DEADLOCK</c>.
    /// </summary>
    Deadlock = 4,
}

/// <summary>
/// Immutable result of <see cref="IDbErrorClassifier.Classify"/>.
/// Carries only what is safe to surface to the API client; the raw
/// provider error number / state code is intentionally NOT included so
/// the wire shape stays stable across provider swaps.
/// </summary>
/// <param name="Kind">The constraint family. Never <c>null</c> for a valid input.</param>
/// <param name="EntityName">
/// Best-effort entity / table name parsed from the provider message
/// (e.g. <c>dbo.TenE0User</c>). <c>null</c> if the provider did not
/// embed one or the classifier could not parse it.
/// </param>
/// <param name="ConstraintName">
/// Best-effort constraint / index name (e.g. <c>UX_Users_UserCode</c>).
/// <c>null</c> if unavailable. Treated as diagnostic — the mapper does
/// not surface it in the default <c>errorMessage</c> unless explicitly
/// opted into by a future <c>IApiErrorMapper</c> overload.
/// </param>
public sealed record DbErrorClassification(
    DbErrorKind Kind,
    string? EntityName = null,
    string? ConstraintName = null)
{
    /// <summary>Default fallback — used when classification fails or the inner is unrecognised.</summary>
    public static DbErrorClassification Unknown() => new(DbErrorKind.Other);
}
