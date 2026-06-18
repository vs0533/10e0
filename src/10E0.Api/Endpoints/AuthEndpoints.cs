using Microsoft.AspNetCore.Http;
using TenE0.Core.Abstractions;
using TenE0.Core.Auth.Jwt.Commands;
using TenE0.Core.Common;
using TenE0.Core.Errors;

namespace TenE0.Api.Endpoints;

internal static class AuthEndpoints
{
    public static WebApplication MapAuthEndpoints(this WebApplication app)
    {
        // #50: 统一 IErrs 校验失败响应到 ApiResult<T> 信封（与异常路径一致）。
        // success 路径也包成 envelope，使客户端可用同一 DTO 反序列化成功/失败。
        app.MapPost("/auth/login", async (LoginCommand cmd, ICommandDispatcher d, IErrs errs, HttpContext http, CancellationToken ct) =>
        {
            var withIp = cmd with { ClientIp = http.Connection.RemoteIpAddress?.ToString() };
            var result = await d.SendAsync(withIp, ct);
            return errs.IsValid
                ? ApiResultResult.Api(ApiResult<object>.Ok(result))
                : ApiResultResult.Api(ApiResult<object>.FromErrs(errs));
        });

        app.MapPost("/auth/refresh", async (RefreshTokenCommand cmd, ICommandDispatcher d, IErrs errs, HttpContext http, CancellationToken ct) =>
        {
            var withIp = cmd with { ClientIp = http.Connection.RemoteIpAddress?.ToString() };
            var result = await d.SendAsync(withIp, ct);
            return errs.IsValid
                ? ApiResultResult.Api(ApiResult<object>.Ok(result))
                : ApiResultResult.Api(ApiResult<object>.FromErrs(errs));
        });

        app.MapPost("/auth/logout", async (LogoutCommand cmd, ICommandDispatcher d, CancellationToken ct) =>
        {
            await d.SendAsync(cmd, ct);
            return Results.Ok(new { ok = true });
        });

        return app;
    }
}
