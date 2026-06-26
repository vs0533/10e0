namespace TenE0.Core.Security.Captcha;

/// <summary>
/// 验证码模块配置（issue #162）。
/// </summary>
public sealed class CaptchaOptions
{
    /// <summary>总开关。关时不强制任何端点校验验证码。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 登录端点验证码触发策略。
    /// <list type="bullet">
    /// <item><see cref="CaptchaTrigger.Disabled"/>：登录不要验证码（默认，避免每次都填）。</item>
    /// <item><see cref="CaptchaTrigger.Always"/>：每次登录都要。</item>
    /// <item><see cref="CaptchaTrigger.AfterFailures"/>：同 IP / 账号失败若干次后才要
    ///   （阈值见 <see cref="AfterFailuresThreshold"/>，配合 LoginProtection 计数）。</item>
    /// </list>
    /// </summary>
    public CaptchaTrigger LoginTrigger { get; set; } = CaptchaTrigger.Disabled;

    /// <summary>
    /// <see cref="CaptchaTrigger.AfterFailures"/> 触发的失败次数阈值（默认 3）。
    /// </summary>
    public int AfterFailuresThreshold { get; set; } = 3;

    /// <summary>验证码 TTL（默认 5 分钟）。超时验证失败。</summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>图形验证码长度（字符数，默认 4）。</summary>
    public int ImageCodeLength { get; set; } = 4;

    /// <summary>图形验证码宽度（像素）。</summary>
    public int ImageWidth { get; set; } = 160;

    /// <summary>图形验证码高度（像素）。</summary>
    public int ImageHeight { get; set; } = 50;

    /// <summary>校验时是否大小写不敏感（默认 true，提升体验）。</summary>
    public bool CaseInsensitive { get; set; } = true;

    /// <summary>滑块验证码距离容差（像素，默认 5）—— 拖动距离与缺口位置差距在此范围内算通过。</summary>
    public int SliderTolerance { get; set; } = 5;
}

/// <summary>
/// 验证码触发策略。
/// </summary>
public enum CaptchaTrigger
{
    /// <summary>不启用（默认）。</summary>
    Disabled,

    /// <summary>每次都要。</summary>
    Always,

    /// <summary>失败若干次后才要（按 IP / 账号计数，阈值见 <see cref="CaptchaOptions.AfterFailuresThreshold"/>）。</summary>
    AfterFailures,
}
