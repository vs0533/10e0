namespace TenE0.Core.Abstractions;

/// <summary>
/// JWT Claim 类型常量。从旧 GlobalConstans.JwtClaimTypes 迁移并收敛。
/// </summary>
public static class JwtClaims
{
    public const string Subject = "sub";
    public const string Name = "name";
    public const string Role = "role";
    public const string UserType = "user_type";
}

/// <summary>
/// 缓存键前缀常量。
/// </summary>
public static class CacheKeys
{
    public const string UserInfo = "user_info";
}
