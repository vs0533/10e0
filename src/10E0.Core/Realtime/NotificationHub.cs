using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace TenE0.Core.Realtime;

/// <summary>
/// 实时推送 Hub（#155）。
///
/// 职责（刻意保持薄）：
/// <list type="bullet">
/// <item><see cref="OnConnectedAsync"/>：经 <see cref="IRealtimeGroupProvider"/> 把连接加入其应属的组
/// （user: / role: / tenant: / org: 等），之后 <c>Clients.Group(name)</c> 即可定向广播。</item>
/// <item><see cref="OnDisconnectedAsync"/>：SignalR 自动移除该连接加入的所有组，无需手动清理。</item>
/// </list>
///
/// 业务方通常不直接调 Hub 方法 —— 推送经 <see cref="IRealtimeNotifier"/>（或实现
/// <see cref="INotifyClient"/> 声明式触发）发起，Hub 仅作为投递通道。
/// </summary>
[Authorize]
public sealed class NotificationHub(
    IRealtimeGroupProvider groupProvider,
    ILogger<NotificationHub> logger) : Hub
{
    public override async Task OnConnectedAsync()
    {
        // Context.User 由 JWT bearer 认证填充（WebSocket 握手前已验签）。
        if (Context.User is { Identity.IsAuthenticated: true })
        {
            var groups = groupProvider.GetGroups(Context.User);
            foreach (var group in groups)
                await Groups.AddToGroupAsync(Context.ConnectionId, group);

            logger.LogDebug("连接 {Conn} 加入 {Count} 个组：{Groups}",
                Context.ConnectionId, groups.Count, string.Join(", ", groups));
        }

        await base.OnConnectedAsync();
    }
}
