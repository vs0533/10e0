using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using TenE0.Core.Common;
using TenE0.Core.Permissions.Behaviors;

namespace TenE0.Core.Errors;

/// <summary>
/// Default <see cref="IApiErrorMapper"/> implementation. Maps the exception
/// families called out in issue #39, with <see cref="DbUpdateException"/>
/// further disambiguated in issue #51 via <see cref="IDbErrorClassifier"/>:
///
/// <list type="bullet">
///   <item><see cref="PermissionDeniedException"/>     → 403 / <c>PERM_DENIED</c></item>
///   <item><see cref="ArgumentException"/>            → 400 / <c>VALIDATION_ERROR</c></item>
///   <item><see cref="InvalidOperationException"/>     → 400 / <c>INVALID_OPERATION</c></item>
///   <item><see cref="DbUpdateConcurrencyException"/> → 409 / <c>CONCURRENCY_CONFLICT</c></item>
///   <item><see cref="DbUpdateException"/> (classifiable inner) → 409 / <c>UNIQUE_CONSTRAINT</c> | <c>FOREIGN_KEY_CONSTRAINT</c> | <c>DB_DEADLOCK</c></item>
///   <item><see cref="DbUpdateException"/> (no/unrecognised inner) → 409 / <c>DB_CONSTRAINT</c></item>
///   <item>any other exception                       → 500 / <c>INTERNAL_ERROR</c> (no stack leak)</item>
/// </list>
///
/// The fallback is a stable, non-leaking "Internal server error" message
/// so that internal paths, SQL fragments, or secrets never reach the
/// client body (the original <see cref="Exception.Message"/> stays
/// available in the server-side logs).
///
/// Two constructors are provided so unit tests can keep instantiating
/// <c>new DefaultApiErrorMapper()</c> without resolving DI, while the
/// production DI path passes the registered <see cref="IDbErrorClassifier"/>
/// so the classifier implementation is honored (host apps can replace
/// it via <c>services.Replace(...)</c>).
/// </summary>
public sealed class DefaultApiErrorMapper : IApiErrorMapper
{
    private const string InternalErrorMessage = "Internal server error";

    private readonly IDbErrorClassifier _classifier;

    /// <summary>
    /// Production ctor — accepts the classifier from DI. Use the
    /// <see cref="ExceptionHandlingExtensions.AddTenE0ExceptionHandler"/>
    /// wiring in production so the host's <see cref="IDbErrorClassifier"/>
    /// registration is honored.
    /// </summary>
    public DefaultApiErrorMapper(IDbErrorClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        _classifier = classifier;
    }

    /// <summary>
    /// Convenience ctor for unit tests and lightweight hosts that do
    /// not need a custom classifier. Falls back to the provider-agnostic
    /// <see cref="DefaultDbErrorClassifier"/> which recognises the
    /// message conventions documented on the classifier itself.
    /// </summary>
    public DefaultApiErrorMapper() : this(new DefaultDbErrorClassifier())
    {
    }

    public (int statusCode, ApiResult<object> body) Map(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            PermissionDeniedException permEx => (
                StatusCodes.Status403Forbidden,
                ApiResult<object>.Fail(permEx.Message, code: "PERM_DENIED")),

            ArgumentException argEx => (
                StatusCodes.Status400BadRequest,
                ApiResult<object>.Fail(StripArgumentExceptionSuffix(argEx), code: "VALIDATION_ERROR")),

            InvalidOperationException invOpEx => (
                StatusCodes.Status400BadRequest,
                ApiResult<object>.Fail(invOpEx.Message, code: "INVALID_OPERATION")),

            DbUpdateException dbEx => MapDbUpdate(dbEx),

            _ => (
                StatusCodes.Status500InternalServerError,
                ApiResult<object>.Fail(InternalErrorMessage, code: "INTERNAL_ERROR")),
        };
    }

    /// <summary>
    /// Disambiguate a <see cref="DbUpdateException"/> via the registered
    /// classifier and emit a per-constraint code. Order matters: the
    /// concurrency subclass must be checked first so an
    /// <see cref="DbUpdateConcurrencyException"/> wrapping a generic inner
    /// is not misclassified as an unknown DB error.
    /// </summary>
    private (int statusCode, ApiResult<object> body) MapDbUpdate(DbUpdateException dbEx)
    {
        var classification = _classifier.Classify(dbEx);
        var (code, message) = BuildCodeAndMessage(classification);

        return (
            StatusCodes.Status409Conflict,
            ApiResult<object>.Fail(message, code: code));
    }

    private static (string code, string message) BuildCodeAndMessage(
        DbErrorClassification classification)
    {
        // Wire code is the canonical identifier clients branch on; the
        // human-readable message carries the entity name (when the
        // classifier could extract one) so the UI can render a precise
        // reason ("Email already in use on TenE0User") rather than a
        // generic "Resource conflict". The constraint name is diagnostic
        // only — intentionally not surfaced to keep the wire shape stable
        // across providers (see IDbErrorClassifier docs).
        //
        // The message is intentionally constructed, not the raw
        // DbUpdateException.Message — that keeps internal SQL fragments
        // / connection strings from leaking through the API surface.
        // Concurrency / deadlock are wrapped in a DbUpdateException by EF
        // Core with no parseable entity name, so the entity suffix is
        // only meaningful for unique-key / FK / generic cases.
        var entitySuffix = string.IsNullOrEmpty(classification.EntityName)
            ? string.Empty
            : $" on {classification.EntityName}";

        return classification.Kind switch
        {
            DbErrorKind.UniqueKey => ("UNIQUE_CONSTRAINT", $"duplicate value{entitySuffix}"),
            DbErrorKind.ForeignKey => ("FOREIGN_KEY_CONSTRAINT", $"referenced record not found{entitySuffix}"),
            DbErrorKind.Concurrency => ("CONCURRENCY_CONFLICT", "record was changed by another user"),
            DbErrorKind.Deadlock => ("DB_DEADLOCK", "database busy, please retry"),
            _ => ("DB_CONSTRAINT", $"database constraint violation{entitySuffix}"),
        };
    }

    /// <summary>
    /// <see cref="ArgumentException.Message"/> appends
    /// <c>" (Parameter 'name')"</c> when the paramName ctor argument is
    /// supplied. That's an implementation detail we don't want to leak
    /// to API consumers — the field name is already encoded in the
    /// form-binding layer if callers need it.
    /// </summary>
    private static string StripArgumentExceptionSuffix(ArgumentException ex)
    {
        var msg = ex.Message;
        var idx = msg.IndexOf(" (Parameter '", StringComparison.Ordinal);
        return idx >= 0 ? msg[..idx] : msg;
    }
}
