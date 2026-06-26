using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TenE0.Core.Security.Captcha;

/// <summary>
/// 验证码模块 DI 扩展（issue #162）。
///
/// <para>
/// 一次性注册：
/// <list type="bullet">
/// <item><see cref="CaptchaOptions"/>（默认值 / 绑定）。</item>
/// <item><see cref="CaptchaStore"/>（持有 <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/> +
///   <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/>，一次性消费语义）。</item>
/// <item><see cref="ICaptchaProvider"/> 默认绑到 <see cref="ImageCaptchaProvider"/>
///   （端点 <c>GET /captcha/image</c> 通过 <c>GetCaptchaProvider(CaptchaKind.Image)</c> 解析具体实现）。</item>
/// <item><see cref="ImageCaptchaProvider"/> / <see cref="SliderCaptchaProvider"/> 同时注册，
///   端点按 kind 选取。业务方可 Replace 任一实现。</item>
/// </list>
/// </para>
/// </summary>
public static class CaptchaExtensions
{
    /// <summary>注册验证码模块（图形 + 滑块）。</summary>
    public static IServiceCollection AddTenE0Captcha(
        this IServiceCollection services,
        Action<CaptchaOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<CaptchaOptions>();

        // Store 持有缓存依赖，Singleton（无状态）
        services.TryAddSingleton<CaptchaStore>();

        // 两种 provider 同时注册，端点按 kind 选取
        services.TryAddSingleton<ImageCaptchaProvider>();
        services.TryAddSingleton<SliderCaptchaProvider>();

        // 默认 ICaptchaProvider → Image（登录默认走图形验证码）
        services.TryAddSingleton<ICaptchaProvider>(sp => sp.GetRequiredService<ImageCaptchaProvider>());

        return services;
    }

    /// <summary>从 <see cref="IConfiguration"/> 的 <c>"Captcha"</c> 节绑定 options 后注册。</summary>
    public static IServiceCollection AddTenE0Captcha(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "Captcha")
    {
        services.Configure<CaptchaOptions>(configuration.GetSection(sectionName));
        return services.AddTenE0Captcha(configure: null);
    }

    /// <summary>
    /// 按 <see cref="CaptchaKind"/> 解析具体 provider（端点用）。
    /// 业务方如果 Replace 了某种 provider，这里仍按 DI 容器中的最新注册解析。
    /// </summary>
    public static ICaptchaProvider GetCaptchaProvider(this IServiceProvider sp, CaptchaKind kind) => kind switch
    {
        CaptchaKind.Image => sp.GetRequiredService<ImageCaptchaProvider>(),
        CaptchaKind.Slider => sp.GetRequiredService<SliderCaptchaProvider>(),
        _ => sp.GetRequiredService<ImageCaptchaProvider>(),
    };
}
