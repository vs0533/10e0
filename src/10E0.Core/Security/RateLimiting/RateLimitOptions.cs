namespace TenE0.Core.Security.RateLimiting;

/// <summary>
/// 限流模块配置（issue #162）。
///
/// <para>
/// 基于 .NET 10 内置 <c>RateLimiter</c>（<c>Microsoft.AspNetCore.RateLimiting</c>），
/// <b>不引入</b> 已废弃的第三方 <c>AspNetCoreRateLimit</c>。按 IP / User / 端点前缀多维度分区，
/// 关键端点（登录 / 刷新 / 文件上传）单独配额，匿名用户默认按 IP 防刷。
/// </para>
///
/// <para>
/// <b>默认规则</b>（<see cref="DefaultRules"/>）已覆盖企业级框架的常见防刷场景；
/// 业务方通过 <c>GlobalRules</c> / <c>EndpointRules</c> 追加或覆盖。
/// </para>
/// </summary>
public sealed class RateLimitOptions
{
    /// <summary>
    /// 总开关。生产环境建议 <c>true</c>；测试 / 本地调试可关。
    /// 关闭时 <c>AddTenE0RateLimiting</c> 仍注册 <see cref="RateLimiter"/> 但所有分区无限额度，
    /// 保证 pipeline 不变（<c>app.UseRateLimiter()</c> 仍可调用，仅不拦截）。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 已认证用户是否绕过全局限流（默认 <c>false</c>）。
    /// <b>注意</b>：仅绕过 <see cref="PartitionKind.Ip"/> 全局规则；
    /// <see cref="EndpointRules"/> 中显式声明的端点规则仍生效（如 <c>/auth/refresh</c> 按用户限流）。
    /// </summary>
    public bool PermitAuthenticatedBypass { get; set; }

    /// <summary>
    /// 全局规则（应用到所有未在 <see cref="EndpointRules"/> 命中的路径）。
    /// 默认装载 <see cref="DefaultRules"/> 中的全局规则。
    /// </summary>
    public List<RateLimitRule> GlobalRules { get; set; } = [.. DefaultRules.Global];

    /// <summary>
    /// 按路径前缀细分的端点规则（最长前缀匹配）。
    /// key 为路径前缀（如 <c>"/auth/login"</c>），大小写敏感、不带 query string。
    /// 默认装载 <see cref="DefaultRules"/> 中关键端点的规则。
    /// </summary>
    public Dictionary<string, List<RateLimitRule>> EndpointRules { get; set; } =
        DefaultRules.Endpoints.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.ToList(),
            StringComparer.Ordinal);

    /// <summary>框架内置默认规则（防撞库 / 刷 token / 刷验证码）。</summary>
    public static class DefaultRules
    {
        /// <summary>全局默认：每 IP 每分钟 100 次。</summary>
        public static IReadOnlyList<RateLimitRule> Global { get; } =
        [
            new RateLimitRule(PartitionKind.Ip, PermitLimit: 100, Window: TimeSpan.FromMinutes(1)),
        ];

        /// <summary>
        /// 关键端点的默认配额：
        /// <list type="bullet">
        /// <item><c>/auth/login</c>：每 IP 每分钟 10 次（防撞库）。</item>
        /// <item><c>/auth/refresh</c>：每用户每分钟 5 次。</item>
        /// <item><c>/captcha/image</c>：每 IP 每分钟 30 次（防刷验证码）。</item>
        /// <item><c>/files/upload</c>：每用户每分钟 30 次。</item>
        /// </list>
        /// </summary>
        public static IReadOnlyDictionary<string, IReadOnlyList<RateLimitRule>> Endpoints { get; } =
            new Dictionary<string, IReadOnlyList<RateLimitRule>>(StringComparer.Ordinal)
            {
                ["/auth/login"] = [new RateLimitRule(PartitionKind.Ip, 10, TimeSpan.FromMinutes(1))],
                ["/auth/refresh"] = [new RateLimitRule(PartitionKind.User, 5, TimeSpan.FromMinutes(1))],
                ["/captcha/image"] = [new RateLimitRule(PartitionKind.Ip, 30, TimeSpan.FromMinutes(1))],
                ["/captcha/slider"] = [new RateLimitRule(PartitionKind.Ip, 30, TimeSpan.FromMinutes(1))],
                ["/files/upload"] = [new RateLimitRule(PartitionKind.User, 30, TimeSpan.FromMinutes(1))],
            };
    }
}

/// <summary>
/// 一条限流规则：分区维度 + 配额 + 窗口。
/// </summary>
/// <param name="Partition">分区维度（IP / User / IP+Endpoint / User+Endpoint）。</param>
/// <param name="PermitLimit">窗口内允许通过的请求数（&gt; 0）。</param>
/// <param name="Window">时间窗口（&gt; <c>TimeSpan.Zero</c>）。</param>
/// <param name="QueueLimit">
/// 排队等待额度上限（默认 0，即立即拒绝）。
/// 设 &gt; 0 时启用 <c>SlidingWindowRateLimiter</c> 排队模式（请求排队等待新额度，超时拒绝）。
/// </param>
public sealed record RateLimitRule(
    PartitionKind Partition,
    int PermitLimit,
    TimeSpan Window,
    int QueueLimit = 0)
{
    /// <summary>是否排队模式（QueueLimit &gt; 0）。</summary>
    public bool IsQueued => QueueLimit > 0;
}

/// <summary>
/// 分区维度枚举。决定 <see cref="PartitionPolicyProvider"/> 用什么字段构造 partition key。
/// </summary>
public enum PartitionKind
{
    /// <summary>按客户端 IP 分区（防匿名刷）。</summary>
    Ip,

    /// <summary>按已认证用户标识分区（<c>User.Identity.Name</c>）。</summary>
    User,

    /// <summary>按 IP + 端点路径联合分区（同 IP 不同端点独立配额）。</summary>
    IpAndEndpoint,

    /// <summary>按用户 + 端点路径联合分区（同用户不同端点独立配额）。</summary>
    UserAndEndpoint,
}
