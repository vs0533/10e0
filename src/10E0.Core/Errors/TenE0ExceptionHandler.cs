using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
///
/// #49 followup: the constructor takes an
/// <see cref="IOptions{JsonOptions}"/> dependency so the error body is
/// serialized with the host's configured
/// <see cref="JsonOptions.SerializerOptions"/> — the SAME options the
/// success path uses (matching naming policy, ignore-null condition,
/// dictionary-key policy, etc.). This eliminates the wire-shape
/// divergence between success and error envelopes.
/// </summary>
public sealed class TenE0ExceptionHandler(
    IApiErrorMapper mapper,
    ILogger<TenE0ExceptionHandler> logger,
    IOptions<JsonOptions> jsonOptions) : IExceptionHandler
{
    /// <summary>
    /// Host-configured JSON options, injected via DI. Step 3/3 reads
    /// <c>jsonOptions.Value.SerializerOptions</c> so the error envelope
    /// is serialized with the SAME <see cref="JsonSerializerOptions"/>
    /// the API host uses for the success path (issue #49).
    /// </summary>
    private readonly IOptions<JsonOptions> _jsonOptions = jsonOptions;

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
            _jsonOptions.Value.SerializerOptions,
            cancellationToken);

        return true;
    }
}
