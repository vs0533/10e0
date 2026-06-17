using TenE0.Core.Auth.Jwt.Storage;

namespace TenE0.Api.Domain;

/// <summary>
/// 业务方扩展的用户实体 — 演示 Identity 模式的核心能力。
/// 继承 TenE0User 后，框架的登录/刷新/JWT 流程自动用 AppUser 类型查询。
/// EF Core TPH：AppUser 和未来其他子类自动同表存储。
/// </summary>
internal sealed class AppUser : TenE0User
{
    public string? Avatar { get; set; }
    public string? Department { get; set; }
    public DateOnly? Birthday { get; set; }
}
