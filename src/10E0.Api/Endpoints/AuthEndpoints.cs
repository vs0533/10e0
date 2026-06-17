using Microsoft.AspNetCore.Http;
using TenE0.Core.Abstractions;
using TenE0.Core.Auth.Jwt.Commands;

namespace TenE0.Api.Endpoints;

internal static class AuthEndpoints
{
    public static WebApplication MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/auth/login", async (LoginCommand cmd, ICommandDispatcher d, IErrs errs, HttpContext http, CancellationToken ct) =>
        {
            var withIp = cmd with { ClientIp = http.Connection.RemoteIpAddress?.ToString() };
            var result = await d.SendAsync(withIp, ct);
            return errs.IsValid
                ? Results.Ok(result)
                : Results.Json(new { error = errs.GetFirstError() }, statusCode: 401);
        });

        app.MapPost("/auth/refresh", async (RefreshTokenCommand cmd, ICommandDispatcher d, IErrs errs, HttpContext http, CancellationToken ct) =>
        {
            var withIp = cmd with { ClientIp = http.Connection.RemoteIpAddress?.ToString() };
            var result = await d.SendAsync(withIp, ct);
            return errs.IsValid
                ? Results.Ok(result)
                : Results.Json(new { error = errs.GetFirstError() }, statusCode: 401);
        });

        app.MapPost("/auth/logout", async (LogoutCommand cmd, ICommandDispatcher d, CancellationToken ct) =>
        {
            await d.SendAsync(cmd, ct);
            return Results.Ok(new { ok = true });
        });

        return app;
    }
}
