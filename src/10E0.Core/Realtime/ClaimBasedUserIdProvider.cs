using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using TenE0.Core.Abstractions;

namespace TenE0.Core.Realtime;

/// <summary>
/// 把 SignalR 连接的 <see cref="HubConnectionContext.UserIdentifier"/> 设为 JWT <c>sub</c> claim 值（userCode）。
///
/// 这是 <c>Clients.User(code).SendAsync(...)</c> 生效的前提 —— 默认 <c>IUserIdProvider</c> 用
/// <c>ClaimTypes.NameIdentifier</c>，但本框架 JWT 主体标识在 <c>sub</c> claim，需显式映射。
///
/// 生命周期：Singleton（无状态）。由 <c>AddSignalR</c> 的 options 注册。
/// </summary>
public sealed class ClaimBasedUserIdProvider : IUserIdProvider
{
    /// <summary>从连接的 ClaimsPrincipal 取 userCode（JWT sub claim）。无 sub 时返回 null（无法按 user 定向推送）。</summary>
    public string? GetUserId(HubConnectionContext connection)
        => connection.User?.FindFirstValue(JwtClaims.Subject);
}
