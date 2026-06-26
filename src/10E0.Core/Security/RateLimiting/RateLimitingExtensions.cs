using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;

namespace TenE0.Core.Security.RateLimiting;

/// <summary>
/// 限流模块 DI / pipeline 扩展（issue #162）。
///
/// <para>
/// 一次性注册 <see cref="RateLimitOptions"/> + ASP.NET Core 内置 <c>RateLimiter</c>：
/// <list type="bullet">
/// <item><c>AddTenE0RateLimiting</c>：注册 options + 挂自定义 partition policy（"tene0-policy"）。
///   按路径前缀最长匹配选规则，按规则 <see cref="PartitionKind"/> 分区构造
///   <c>FixedWindowRateLimiter</c> / <c>SlidingWindowRateLimiter</c>。</item>
/// <item><c>UseTenE0RateLimiting</c>：等价 <c>app.UseRateLimiter()</c>，仅命名上与其它模块一致。
///   必须放在 <c>UseAuthentication</c> 之后（否则拿不到 user 分区）。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>多分区叠加</b>：ASP.NET Core 的 <c>RateLimiter</c> 内置只支持一个 partition per request，
/// 故本扩展取命中的<b>首条</b>规则（多条规则时按规则列表顺序，第一条决定实际配额）。
/// 若需"IP + User 双重限流"，业务方可在 options 中追加 <see cref="PartitionKind.IpAndEndpoint"/> 等
/// 细分维度，或自行 <c>services.Configure&lt;RateLimiterOptions&gt;</c> 叠加自定义分区。
/// </para>
/// </summary>
public static class RateLimitingExtensions
{
    /// <summary>自定义分区策略名（供端点用 <c>RequireRateLimiting("tene0-policy")</c> 显式启用）。</summary>
    public const string PolicyName = "tene0-policy";

    /// <summary>
    /// 注册限流基础设施 + 默认分区策略。调用方需再调 <see cref="UseTenE0RateLimiting"/> 接入 pipeline。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="configure">覆盖 <see cref="RateLimitOptions"/>；不传用默认。</param>
    public static IServiceCollection AddTenE0RateLimiting(
        this IServiceCollection services,
        Action<RateLimitOptions>? configure = null)
    {
        var optionsBuilder = configure is not null
            ? services.AddOptions<RateLimitOptions>().Configure(configure)
            : services.AddOptions<RateLimitOptions>();

        // 测试环境自动关闭限流：WebApplicationFactory 的所有 HTTP 请求共享 127.0.0.1 源 IP，
        // 而 /auth/login 默认每 IP 每分钟 10 次 —— 测试类内多次登录会被 429 误伤（#162）。
        // Test 环境无真实流量压力，关闭限流让集成测试聚焦被测业务行为。
        // Configure<IServiceProvider>：DI 容器恒可解析 IServiceProvider；用 GetService
        // 弱引用 IWebHostEnvironment（裸 ServiceCollection 单元测试无 web 环境时不抛）。
        optionsBuilder.Configure<IServiceProvider>((opt, sp) =>
        {
            var env = sp.GetService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
            if (env is not null && string.Equals(env.EnvironmentName, "Test", StringComparison.Ordinal))
                opt.Enabled = false;
        });

        services.AddRateLimiter(opt =>
        {
            opt.RejectionStatusCode = 429; // StatusCodes.Status429TooManyRequests
            opt.OnRejected = RateLimitResponseWriter.OnRejectedAsync;

            opt.AddPolicy(PolicyName, httpContext =>
            {
                var options = httpContext.RequestServices
                    .GetRequiredService<IOptions<RateLimitOptions>>().Value;

                // 关闭限流：返回"无限额度"分区（FixedWindow 极大 PermitLimit + 极长 Window）。
                // 用 GetFixedWindowLimiter 而非直接返回 null —— AddPolicy 回调必须返回非 null partition。
                if (!options.Enabled)
                {
                    return RateLimitPartition.GetNoLimiter("disabled");
                }

                var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var path = httpContext.Request.Path.Value ?? "/";
                var isAuthenticated = httpContext.User.Identity?.IsAuthenticated == true;
                var user = isAuthenticated ? httpContext.User.Identity?.Name : null;

                // PermitAuthenticatedBypass：已认证用户跳过全局规则，仅端点级显式规则生效。
                var rules = PartitionPolicyProvider.ResolveRules(path, options);
                if (options.PermitAuthenticatedBypass && isAuthenticated)
                {
                    // 端点规则中显式声明 User/UserAndEndpoint 的规则保留，全局 / IP 规则跳过。
                    rules = rules
                        .Where(r => r.Partition is PartitionKind.User or PartitionKind.UserAndEndpoint)
                        .ToList();
                    if (rules.Count == 0)
                    {
                        return RateLimitPartition.GetNoLimiter($"bypass:{user}");
                    }
                }

                // ASP.NET Core RateLimiter 一个请求只接受一个 partition，取首条规则。
                var primary = rules[0];
                var partitions = PartitionPolicyProvider.BuildPartitions(ip, user, path, [primary]);
                return partitions[0];
            });
        });

        return services;
    }

    /// <summary>
    /// 从 <see cref="IConfiguration"/> 的 <c>"RateLimiting"</c> 节绑定 options 后注册。
    /// </summary>
    public static IServiceCollection AddTenE0RateLimiting(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "RateLimiting")
    {
        services.Configure<RateLimitOptions>(configuration.GetSection(sectionName));
        return services.AddTenE0RateLimiting(configure: null);
    }

    /// <summary>
    /// 接入限流 pipeline。等价 <c>app.UseRateLimiter()</c>，命名上与其它模块对齐。
    /// 必须放在 <c>UseRouting</c> 之后、<c>UseAuthentication</c> 之后（这样 user 分区可用）。
    /// </summary>
    public static IApplicationBuilder UseTenE0RateLimiting(this IApplicationBuilder app)
        => app.UseRateLimiter();
}
