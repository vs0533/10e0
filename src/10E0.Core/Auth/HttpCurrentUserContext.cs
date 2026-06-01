using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using TenE0.Core.Abstractions;

namespace TenE0.Core.Auth;

/// <summary>
/// HTTP 场景下的 ICurrentUserContext 实现。
///
/// 与旧 AuthFactory + DefaultAuth/UnitAuth + E0Context.CurrentUser 的对比：
/// - 旧方案：三层调用链 + Scoped 缓存 + 循环引用 + .Result 阻塞
/// - 新方案：单一类，直接读 ClaimsPrincipal，零状态字段，零阻塞
///
/// 用户类型分支（个人/单位）通过 ClaimsPrincipal 上的 UserType claim 区分，
/// 不再需要工厂模式实例化不同的 IAuth 子类。
/// </summary>
internal sealed class HttpCurrentUserContext(
    IHttpContextAccessor httpContextAccessor,
    IDistributedCache cache,
    IUserInfoLoader userInfoLoader) : ICurrentUserContext
{
    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public string? UserCode => Principal?.FindFirstValue(JwtClaims.Subject);

    public UserType UserType =>
        Enum.TryParse<UserType>(Principal?.FindFirstValue(JwtClaims.UserType), out var t)
            ? t
            : UserType.Person;

    public IReadOnlyList<string> RoleIds =>
        Principal?.FindAll(JwtClaims.Role).Select(c => c.Value).ToList() ?? [];

    public async ValueTask<ICurrentUserInfo?> GetUserInfoAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAuthenticated || UserCode is null)
            return null;

        var cacheKey = $"{CacheKeys.UserInfo}:{UserCode}";

        // 缓存命中即返回，未命中走数据加载器（避免本类直接耦合 DbContext）
        var cached = await cache.GetStringAsync(cacheKey, cancellationToken);
        if (cached is not null)
            return userInfoLoader.Deserialize(cached, UserType);

        var info = await userInfoLoader.LoadAsync(UserCode, UserType, cancellationToken);
        if (info is not null)
        {
            await cache.SetStringAsync(
                cacheKey,
                userInfoLoader.Serialize(info),
                cancellationToken);
        }

        return info;
    }
}

/// <summary>
/// 用户信息加载策略。由实现项目（例如 10E0.Api 或业务项目）提供。
///
/// 这一层抽象让 HttpCurrentUserContext 不直接依赖 DbContext，
/// 避免 Auth 与数据访问层循环引用（旧实现的痛点之一）。
/// </summary>
public interface IUserInfoLoader
{
    ValueTask<ICurrentUserInfo?> LoadAsync(string userCode, UserType userType, CancellationToken cancellationToken);
    string Serialize(ICurrentUserInfo info);
    ICurrentUserInfo? Deserialize(string payload, UserType userType);
}
