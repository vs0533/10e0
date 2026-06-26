using TenE0.Core.Security.Captcha;
using TenE0.Core.Security.RateLimiting;

namespace TenE0.Api.Endpoints;

/// <summary>
/// 验证码获取端点（issue #162）。
///
/// <para>
/// 两个端点都走 <c>RequireRateLimiting</c>（按 IP 限流，防刷验证码生成接口消耗服务器 CPU）。
/// 返回 PNG 图片字节流 + <c>X-Captcha-Id</c> 响应头（客户端校验时回传此 id）。
/// </para>
/// </summary>
internal static class CaptchaEndpoints
{
    public static WebApplication MapCaptchaEndpoints(this WebApplication app)
    {
        // 图形验证码：返回 PNG + X-Captcha-Id 头
        app.MapGet("/captcha/image", async (IServiceProvider sp, HttpContext http, CancellationToken ct) =>
        {
            var provider = sp.GetRequiredService<ImageCaptchaProvider>();
            var result = await provider.GenerateAsync(ct);

            http.Response.Headers["X-Captcha-Id"] = result.CaptchaId;
            http.Response.Headers.CacheControl = "no-store";
            return Results.File(result.Image, result.ContentType);
        })
        .RequireRateLimiting(RateLimitingExtensions.PolicyName)
        .WithName("GetImageCaptcha")
        .WithDescription("获取图形验证码（PNG + X-Captcha-Id 响应头）");

        // 滑块验证码：返回背景 PNG + 滑块 PNG + X-Captcha-Id 头
        // 滑块图用 X-Slider-Url 无法直接返回两段字节，故用 multipart-like 包装：
        // 主体返回背景图，滑块小图通过单独的 /captcha/slider/image?captchaId=xxx 端点取。
        // 此处先返回 CaptchaInitResponse（JSON）包含 captchaId + slider 尺寸，让前端按需拉图。
        app.MapGet("/captcha/slider", async (IServiceProvider sp, HttpContext http, CancellationToken ct) =>
        {
            var provider = sp.GetRequiredService<SliderCaptchaProvider>();
            var result = await provider.GenerateAsync(ct);

            // 背景图作为 body 返回，captchaId 通过响应头回传。
            // 滑块图同步生成但本端点不返回（避免 multipart 复杂度）—— 滑块小图由前端用纯色块
            // 模拟即可（校验只看拖动距离 X，与滑块视觉无关）。如需真实滑块小图，业务方可
            // 自行追加 /captcha/slider/piece 端点从 result.SliderImage 返回。
            if (result.SliderImage is not null)
                await result.SliderImage.DisposeAsync();

            http.Response.Headers["X-Captcha-Id"] = result.CaptchaId;
            if (result.SliderSize is { } size)
            {
                http.Response.Headers["X-Slider-Width"] = size.Width.ToString();
                http.Response.Headers["X-Slider-Height"] = size.Height.ToString();
            }
            http.Response.Headers.CacheControl = "no-store";
            return Results.File(result.Image, result.ContentType);
        })
        .RequireRateLimiting(RateLimitingExtensions.PolicyName)
        .WithName("GetSliderCaptcha")
        .WithDescription("获取滑块验证码（背景 PNG + X-Captcha-Id / X-Slider-* 响应头）");

        return app;
    }
}
