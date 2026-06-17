using Microsoft.AspNetCore.Http;
using TenE0.Core.Common;

namespace TenE0.Core.Errors;

/// <summary>
/// Minimal API glue: force every endpoint to emit the uniform
/// <c>ApiResult&lt;T&gt;</c> envelope (issue #39).
///
/// Usage inside an endpoint:
/// <code>
/// var data = await dispatcher.SendAsync(query, ct);
/// return ApiResultResult.Api(ApiResult&lt;Demo&gt;.Ok(data));
/// </code>
/// </summary>
public static class ApiResultResult
{
    /// <summary>
    /// Map an <see cref="ApiResult{T}"/> to the correct HTTP status:
    /// <c>200 OK</c> on success, <c>400 Bad Request</c> on failure.
    /// Domain-level exceptions (permission / DB / unhandled) are caught
    /// by <see cref="TenE0ExceptionHandler"/> upstream, so the only
    /// business failure path this method needs to know about is the
    /// <c>IErrs</c>-driven <c>Success = false</c> envelope.
    /// </summary>
    public static IResult Api<T>(ApiResult<T> result) =>
        result.success ? Results.Ok(result) : Results.BadRequest(result);
}
