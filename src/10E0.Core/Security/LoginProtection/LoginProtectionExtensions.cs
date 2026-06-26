using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TenE0.Core.Security.LoginProtection;

/// <summary>
/// 登录保护模块 DI 扩展（issue #162）。
///
/// <para>
/// 一次性注册：
/// <list type="bullet">
/// <item><see cref="LoginProtectionOptions"/>（绑定 / 默认值）。</item>
/// <item><see cref="ILoginAttemptStore"/> → 默认 <see cref="InMemoryLoginAttemptStore"/>
///   （多副本部署必须 <c>services.Replace(...)</c> 切到 Redis <c>INCR</c> 实现）。</item>
/// <item><see cref="LoginProtector"/>（Scoped，持有 Scoped <see cref="Microsoft.Extensions.Options.IOptions{LoginProtectionOptions}"/>
///   与 Singleton <see cref="TimeProvider"/>）。</item>
/// </list>
/// </para>
/// </summary>
public static class LoginProtectionExtensions
{
    /// <summary>
    /// 注册登录保护（失败锁定）模块。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="configure">覆盖 <see cref="LoginProtectionOptions"/>；不传用默认。</param>
    public static IServiceCollection AddTenE0LoginProtection(
        this IServiceCollection services,
        Action<LoginProtectionOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<LoginProtectionOptions>();

        // 默认进程内存储；多副本 Replace 为 Redis 实现
        services.TryAddSingleton<ILoginAttemptStore, InMemoryLoginAttemptStore>();

        // LoginProtector 是无状态服务，但生命周期跟随请求（让业务方按需 Replace 为 Scoped 包装）
        services.TryAddScoped<LoginProtector>();

        return services;
    }

    /// <summary>从 <see cref="IConfiguration"/> 的 <c>"LoginProtection"</c> 节绑定 options 后注册。</summary>
    public static IServiceCollection AddTenE0LoginProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "LoginProtection")
    {
        services.Configure<LoginProtectionOptions>(configuration.GetSection(sectionName));
        return services.AddTenE0LoginProtection(configure: null);
    }
}
