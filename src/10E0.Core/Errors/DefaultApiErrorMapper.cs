using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using TenE0.Core.Common;
using TenE0.Core.Permissions.Behaviors;

namespace TenE0.Core.Errors;

/// <summary>
/// Default <see cref="IApiErrorMapper"/> implementation. Maps the five
/// exception families called out in issue #39:
///
/// <list type="bullet">
///   <item><see cref="PermissionDeniedException"/>     → 403 / <c>PERM_DENIED</c></item>
///   <item><see cref="ArgumentException"/>            → 400 / <c>VALIDATION_ERROR</c></item>
///   <item><see cref="InvalidOperationException"/>     → 400 / <c>INVALID_OPERATION</c></item>
///   <item><see cref="DbUpdateException"/>            → 409 / <c>CONFLICT</c></item>
///   <item>any other exception                       → 500 / <c>INTERNAL_ERROR</c> (no stack leak)</item>
/// </list>
///
/// The fallback is a stable, non-leaking "Internal server error" message
/// so that internal paths, SQL fragments, or secrets never reach the
/// client body (the original <see cref="Exception.Message"/> stays
/// available in the server-side logs).
/// </summary>
public sealed class DefaultApiErrorMapper : IApiErrorMapper
{
    private const string InternalErrorMessage = "Internal server error";

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

            DbUpdateException => (
                StatusCodes.Status409Conflict,
                ApiResult<object>.Fail("Resource conflict", code: "CONFLICT")),

            _ => (
                StatusCodes.Status500InternalServerError,
                ApiResult<object>.Fail(InternalErrorMessage, code: "INTERNAL_ERROR")),
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
