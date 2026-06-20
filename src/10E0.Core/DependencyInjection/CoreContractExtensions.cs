using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenE0.Core.Abstractions;

namespace TenE0.Core.DependencyInjection;

/// <summary>
/// 注册 Core 的"全局字符串契约"注入点（#37）。
///
/// 三类原本硬编的全局字符串（JWT claim 名 / 缓存 key 命名空间）现在各自有接口 + 默认
/// 实现，业务项目可一行 <c>Replace</c> 切换为自家 IdP / 多租户 namespace 方案，
/// 无需改 Core 源码。
///
/// <para>错误码：与另两项不同，<see cref="ErrorCodes"/> 是 <c>static class</c> +
/// <c>const string</c> 编译期常量（前端 i18n 需静态枚举），不需要 DI 注册 ——
/// 业务方直接 <c>ErrorCodes.AuthInvalid</c> 引用即可。</para>
///
/// 推荐用法：在业务入口 <c>Program.cs</c> / <c>Startup.cs</c> 中
/// <c>builder.Services.AddTenE0CoreContracts(...)</c>，或在自定义 <see cref="IAppModule"/>
/// 的 <c>ConfigureServices</c> 中调用。
///
/// 此扩展不影响 <see cref="JwtClaims"/> / <c>CacheKeys</c> 静态常量类
/// —— 它们仍可被旧代码直接引用，保证平滑迁移。
/// </summary>
public static class CoreContractExtensions
{
    /// <summary>
    /// 注册 <see cref="ITokenClaimNames"/> / <see cref="ICacheKeyNamespace"/> 的默认实现。
    ///
    /// 使用 <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService,TImplementation}"/>
    /// 确保已有注册（如业务模块的 <c>Replace</c>）不被覆盖。
    /// </summary>
    /// <returns>同一 <paramref name="services"/> 实例，链式调用友好。</returns>
    public static IServiceCollection AddTenE0CoreContracts(this IServiceCollection services)
    {
        // JWT claim 名 — Keycloak / Auth0 / SAML 可整体替换
        services.TryAddSingleton<ITokenClaimNames, JwtClaimsTokenClaimNames>();

        // 缓存 key namespace — 多租户场景注入 tenantId 前缀
        services.TryAddSingleton<ICacheKeyNamespace, DefaultCacheKeyNamespace>();

        return services;
    }
}
