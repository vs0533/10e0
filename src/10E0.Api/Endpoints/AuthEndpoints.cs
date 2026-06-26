using Microsoft.Extensions.Options;
using TenE0.Core.Abstractions;
using TenE0.Core.Auth.Jwt.Commands;
using TenE0.Core.Common;
using TenE0.Core.Errors;
using TenE0.Core.Security.Captcha;
using TenE0.Core.Security.LoginProtection;
using TenE0.Core.Security.RateLimiting;
namespace TenE0.Api.Endpoints;

internal static class AuthEndpoints
{
    public static WebApplication MapAuthEndpoints(this WebApplication app)
    {
        // #50: 统一 IErrs 校验失败响应到 ApiResult<T> 信封（与异常路径一致）。
        // success 路径也包成 envelope，使客户端可用同一 DTO 反序列化成功/失败。
        //
        // #162: 登录端点接入 (a) 限流：RequireRateLimiting 按路径前缀走 tene0-policy；
        //                  (b) 验证码：按 CaptchaOptions.LoginTrigger 决定是否强制校验；
        //                  (c) 失败锁定：LoginProtector 在 LoginCommandHandler 内部处理。
        app.MapPost("/auth/login", async (
            LoginCommand cmd,
            ICommandDispatcher d,
            IErrs errs,
            HttpContext http,
            IServiceProvider sp,
            CancellationToken ct) =>
        {
            // #162 验证码校验：按 CaptchaOptions.LoginTrigger 决定是否强制。
            // Disabled（默认）→ 跳过；Always → 必校验；AfterFailures → 仅在 LoginProtector
            // 失败计数 ≥ 阈值时强制（防"每次都让用户填"的体验损耗）。
            var captchaValidated = await EnsureCaptchaAsync(sp, cmd, errs, ct);
            if (!captchaValidated)
            {
                return ApiResultResult.Api(ApiResult<object>.FromErrs(errs));
            }

            var withIp = cmd with { ClientIp = http.Connection.RemoteIpAddress?.ToString() };
            var result = await d.SendAsync(withIp, ct);
            return errs.IsValid
                ? ApiResultResult.Api(ApiResult<object>.Ok(result))
                : ApiResultResult.Api(ApiResult<object>.FromErrs(errs));
        })
        .RequireRateLimiting(RateLimitingExtensions.PolicyName);

        app.MapPost("/auth/refresh", async (RefreshTokenCommand cmd, ICommandDispatcher d, IErrs errs, HttpContext http, CancellationToken ct) =>
        {
            var withIp = cmd with { ClientIp = http.Connection.RemoteIpAddress?.ToString() };
            var result = await d.SendAsync(withIp, ct);
            return errs.IsValid
                ? ApiResultResult.Api(ApiResult<object>.Ok(result))
                : ApiResultResult.Api(ApiResult<object>.FromErrs(errs));
        })
        .RequireRateLimiting(RateLimitingExtensions.PolicyName);

        app.MapPost("/auth/logout", async (LogoutCommand cmd, ICommandDispatcher d, CancellationToken ct) =>
        {
            await d.SendAsync(cmd, ct);
            return Results.Ok(new { ok = true });
        });

        return app;
    }

    /// <summary>
    /// #162 按配置策略执行验证码校验。返回 true 表示放行（无需校验或校验通过），
    /// false 表示已写入 errs（调用方应短路返回失败响应）。
    /// </summary>
    private static async Task<bool> EnsureCaptchaAsync(
        IServiceProvider sp,
        LoginCommand cmd,
        IErrs errs,
        CancellationToken ct)
    {
        // 未注册验证码模块 → 直接放行（保持向后兼容）。
        var captchaOptions = sp.GetService<IOptions<CaptchaOptions>>()?.Value;
        if (captchaOptions is null || !captchaOptions.Enabled) return true;

        var trigger = captchaOptions.LoginTrigger;
        if (trigger == CaptchaTrigger.Disabled) return true;

        // AfterFailures：仅在 LoginProtector 失败计数达阈值时才要验证码。
        // 未启用 LoginProtection 时退化为 Always（让验证码仍生效）。
        if (trigger == CaptchaTrigger.AfterFailures)
        {
            var protector = sp.GetService<LoginProtector>();
            if (protector is null)
            {
                trigger = CaptchaTrigger.Always;
            }
            else
            {
                var state = await protector.EnsureNotLockedAsync(cmd.UserCode, ct);
                if (state.FailedCount < captchaOptions.AfterFailuresThreshold)
                    return true; // 失败次数未达阈值，本次不要验证码
                trigger = CaptchaTrigger.Always;
            }
        }

        // Always：强制校验。无 captchaId/code → CAPTCHA_REQUIRED；校验失败 → CAPTCHA_INVALID。
        var provider = sp.GetService<ICaptchaProvider>();
        if (provider is null)
        {
            // 配置要求验证码但未注册 ICaptchaProvider —— 视为模块装配不完整，fail-closed 拒绝。
            errs.Add("验证码服务未启用，无法登录", code: ErrorCodes.CaptchaRequired);
            return false;
        }

        if (string.IsNullOrEmpty(cmd.CaptchaId) || string.IsNullOrEmpty(cmd.CaptchaCode))
        {
            errs.Add("请输入验证码", code: ErrorCodes.CaptchaRequired);
            return false;
        }

        var ok = await provider.ValidateAsync(cmd.CaptchaId!, cmd.CaptchaCode!, ct);
        if (!ok)
        {
            errs.Add("验证码错误或已过期", code: ErrorCodes.CaptchaInvalid);
            return false;
        }

        return true;
    }
}
