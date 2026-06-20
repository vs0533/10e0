using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TenE0.Core.Common;

namespace TenE0.Core.Errors;

/// <summary>
/// ASP.NET Core 8+ <see cref="IExceptionHandler"/> implementation that
/// funnels every unhandled exception through <see cref="IApiErrorMapper"/>
/// and writes the resulting <see cref="ApiResult{T}"/> body to the response.
///
/// Wire-up:
/// <code>
/// services.AddTenE0ExceptionHandler();
/// ...
/// app.UseExceptionHandler();   // dispatches to registered IExceptionHandler
/// </code>
///
/// Once the handler is registered, every endpoint gets the same uniform
/// JSON error envelope — no more per-handler try/catch blocks (issue #39).
/// </summary>
public sealed class TenE0ExceptionHandler(
    IApiErrorMapper mapper,
    ILogger<TenE0ExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>
    /// JSON options used when writing the error body. Mirrors the API
    /// project's default (lowerCamelCase) so the wire shape matches the
    /// success-path <c>ApiResult</c> envelope.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var (statusCode, body) = mapper.Map(exception);

        // 5xx is the mapper's signal that the original exception is unexpected
        // — log it (with stack) on the server so operators have full context,
        // but the client body stays a safe "Internal server error".
        if (statusCode >= 500)
        {
            logger.LogError(
                exception,
                "Unhandled exception mapped to {StatusCode} {ErrorCode}",
                statusCode, body.errorCode);
        }
        else
        {
            // 4xx is caller-induced; log at Information so we can audit
            // repeated PERM_DENIED etc. without polluting the error stream.
            logger.LogInformation(
                "Client error {StatusCode} {ErrorCode}: {Message}",
                statusCode, body.errorCode, exception.Message);
        }

        // Reject any partial response so a streaming endpoint can't leak
        // a partial body before the error envelope is written.
        httpContext.Response.Clear();
        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/json; charset=utf-8";

        await JsonSerializer.SerializeAsync(
            httpContext.Response.Body,
            body,
            JsonOptions,
            cancellationToken);

        return true;
    }
}
