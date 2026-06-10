namespace TenE0.Core.Abstractions;

/// <summary>
/// 当前用户上下文。
///
/// 设计要点（对比旧 E0Context.CurrentUser）：
/// - 同步属性只读 ClaimsPrincipal，零 I/O，零阻塞
/// - 仅当需要"用户详情对象"时才异步加载（GetUserInfoAsync），可控、可缓存
/// - 无 .Result 调用，无副作用 getter
/// - 不依赖 E0Context，可以独立注入到任何服务
/// </summary>
public interface ICurrentUserContext
{
    /// <summary>是否已认证（直接从 ClaimsPrincipal 读，无 I/O）。</summary>
    bool IsAuthenticated { get; }

    /// <summary>用户唯一编码（来自 JWT subject claim）。未登录返回 null。</summary>
    string? UserCode { get; }

    /// <summary>用户类型（个人/单位）。来自 JWT 自定义 claim。</summary>
    UserType UserType { get; }

    /// <summary>角色 ID 列表。来自 JWT role claims。</summary>
    IReadOnlyList<string> RoleIds { get; }

    /// <summary>
    /// 签发 JWT 时各角色的版本号快照。来自 <see cref="JwtClaims.RoleVersion"/> claim，
    /// 序列化为 <c>Dictionary&lt;string, long&gt;</c>。
    /// 空字典表示该 token 在 #7 之前签发（legacy），应继续放行不 deny。
    /// </summary>
    IReadOnlyDictionary<string, long> RoleVersions { get; }

    /// <summary>
    /// 异步加载用户详情对象。
    /// 实现层负责走分布式缓存（命中即返回，未命中查 DB 并回填）。
    /// </summary>
    ValueTask<ICurrentUserInfo?> GetUserInfoAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 用户类型枚举。
/// 旧 E0 用此区分个人账号 vs 单位/机构账号，新版保留该模型（高价值的领域区分）。
/// </summary>
public enum UserType
{
    Person = 0,
    Unit = 1
}

/// <summary>
/// 用户详情对象的最小契约。具体业务字段由实现项目扩展。
/// </summary>
public interface ICurrentUserInfo
{
    string UserCode { get; }
    string DisplayName { get; }
    UserType UserType { get; }
}
