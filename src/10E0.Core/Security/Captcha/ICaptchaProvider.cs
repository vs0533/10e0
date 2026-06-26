namespace TenE0.Core.Security.Captcha;

/// <summary>
/// 验证码类型枚举。
/// </summary>
public enum CaptchaKind
{
    /// <summary>图形验证码（图片 + 文本）。</summary>
    Image,

    /// <summary>滑块验证码（缺口图 + 滑块图 + 拖动距离校验）。</summary>
    Slider,
}

/// <summary>
/// 验证码生成结果。
/// </summary>
/// <param name="CaptchaId">验证码标识（客户端持有，校验时回传）。</param>
/// <param name="ContentType">图片 MIME（如 <c>"image/png"</c>）。</param>
/// <param name="Image">图片字节流（图形码 / 滑块背景图）。</param>
/// <param name="Kind">验证码类型。</param>
/// <param name="SliderImage">滑块验证码的滑块小图（透底 PNG）；图形验证码此字段为 <c>null</c>。</param>
/// <param name="SliderSize">滑块图尺寸（宽，高），用于客户端 UI 拼合；非滑块为 <c>null</c>。</param>
public sealed record CaptchaResult(
    string CaptchaId,
    string ContentType,
    Stream Image,
    CaptchaKind Kind,
    Stream? SliderImage = null,
    (int Width, int Height)? SliderSize = null);

/// <summary>
/// 验证码抽象（issue #162）。
///
/// <para>
/// 默认实现 <see cref="ImageCaptchaProvider"/> / <see cref="SliderCaptchaProvider"/>；
/// 业务方可 <c>services.Replace(...)</c> 切到第三方（极验 / 阿里云 / Cloudflare Turnstile）实现，
/// 替换 OCR 防护算法而不改接入点。
/// </para>
///
/// <para><b>一次性消费</b>：<see cref="ValidateAsync"/> 通过后立即删除缓存项，
/// 防止同一个验证码被重放多次。</para>
/// </summary>
public interface ICaptchaProvider
{
    /// <summary>本 provider 的验证码类型。</summary>
    CaptchaKind Kind { get; }

    /// <summary>
    /// 生成验证码：生成图片 + 把答案落缓存（key=<paramref name="captchaId"/> 返回的 id）。
    /// </summary>
    Task<CaptchaResult> GenerateAsync(CancellationToken ct = default);

    /// <summary>
    /// 校验：从缓存取答案比对，命中后立即删除（一次性）。
    /// </summary>
    /// <param name="captchaId">生成时返回的 <see cref="CaptchaResult.CaptchaId"/>。</param>
    /// <param name="userInput">
    /// 用户输入。图形验证码为字符文本；滑块为拖动距离（像素字符串）。
    /// </param>
    /// <returns>true = 校验通过；false = id 不存在 / 已过期 / 答案不符。</returns>
    Task<bool> ValidateAsync(string captchaId, string userInput, CancellationToken ct = default);
}
