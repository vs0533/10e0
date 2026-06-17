using TenE0.Core.Common;

namespace TenE0.Core.Errors;

/// <summary>
/// Maps a domain/framework exception to a deterministic
/// (HTTP status code, ApiResult&lt;object&gt;) tuple.
///
/// Centralizing this mapping keeps the HTTP error shape uniform across
/// every endpoint — no more per-handler try/catch blocks emitting
/// inconsistent <c>{ error: "..." }</c> payloads (issue #39).
///
/// Implementations MUST be deterministic, side-effect-free, and MUST NOT
/// leak raw exception messages for unmapped exception types.
/// </summary>
public interface IApiErrorMapper
{
    /// <summary>
    /// Translate <paramref name="exception"/> to a status code and an
    /// <see cref="ApiResult{T}"/> body. Throws
    /// <see cref="ArgumentNullException"/> when <paramref name="exception"/>
    /// is null — null inputs indicate a system-bug, not a user error.
    /// </summary>
    (int statusCode, ApiResult<object> body) Map(Exception exception);
}
