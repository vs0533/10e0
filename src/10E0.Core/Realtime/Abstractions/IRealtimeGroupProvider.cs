using System.Security.Claims;

namespace TenE0.Core.Realtime;

/// <summary>
/// 把一个已连接客户端（<see cref="ClaimsPrincipal"/>）映射到它应加入的 SignalR 组（#155）。
///
/// 默认实现 <see cref="ClaimBasedGroupProvider"/> 从 JWT claims 零 I/O 派生
/// <c>user:</c> / <c>role:</c> / <c>tenant:</c> / <c>org:</c> 组。
/// 业务方可替换本接口加入自定义组（如 <c>project:{id}</c>，需 DB 查询）。
///
/// 注意：Hub 上下文中 <c>IHttpContextAccessor.HttpContext</c> 为 null（WS 握手后），
/// 实现必须基于传入的 <paramref name="user"/>（即 Hub 的 <c>Context.User</c>）读取，
/// 不能注入 <c>ICurrentUserContext</c> / <c>ITenantContext</c>。
/// </summary>
public interface IRealtimeGroupProvider
{
    /// <summary>
    /// 返回该连接应加入的组名列表。连接建立时由 <see cref="NotificationHub.OnConnectedAsync"/> 调用。
    /// </summary>
    /// <param name="user">连接的 ClaimsPrincipal（来自 JWT bearer token）。</param>
    IReadOnlyList<string> GetGroups(ClaimsPrincipal user);
}
