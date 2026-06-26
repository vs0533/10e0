namespace TenE0.Core.Security.LoginProtection;

/// <summary>
/// 登录失败锁定配置（issue #162）。
///
/// <para>
/// 滑动窗口内累计失败次数达到 <see cref="MaxFailedAttempts"/> 后锁定账号 <see cref="LockoutDuration"/>；
/// 锁定期内拒绝任何登录尝试，过期后自动解锁且计数清零。
/// 成功登录立即清空计数（防止"偶尔输错几次 → 累积 → 误锁"）。
/// </para>
///
/// <para>
/// <b>与 #153 系统参数集成</b>：<see cref="MaxFailedAttempts"/> / <see cref="LockoutDuration"/>
/// 可由业务方在 <see cref="LoginProtector"/> 之外用 <c>ISystemParameterStore</c> 动态覆盖
/// （运维改值无需发版）；本类只承担默认值 + 结构。
/// </para>
/// </summary>
public sealed class LoginProtectionOptions
{
    /// <summary>总开关。关时 <see cref="LoginProtector"/> 不锁定（仍计数但不阻断）。</summary>
    public bool LockoutEnabled { get; set; } = true;

    /// <summary>
    /// 滑动窗口内最大失败次数。达到此值后触发锁定。
    /// 建议 5（足够容错偶发输错，又能挡住暴力撞库）。
    /// </summary>
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>
    /// 锁定时长。默认 15 分钟，业务方可调短（高频 API 场景）或加长（敏感账户）。
    /// </summary>
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// 计数滑动窗口。超过此窗口的失败不计入当前累计（避免"半年内累计 5 次"误锁）。
    /// 默认 10 分钟 —— 与 5 次失败配额共同构成"10 分钟内错 5 次锁 15 分钟"的基线策略。
    /// </summary>
    public TimeSpan SlidingWindow { get; set; } = TimeSpan.FromMinutes(10);
}
