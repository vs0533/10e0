namespace TenE0.Core.Realtime;

/// <summary>
/// 实时推送门面（#155）。
///
/// 业务代码通常不需要直接用本接口 —— 实现领域事件 <see cref="INotifyClient"/> 即由
/// <see cref="NotificationDispatcher"/> 自动推送（声明式触发）。本接口供需要"绕过事件直接推送"
/// 的场景使用（如长任务进度、外部系统回调）。
///
/// 实现负责本地直推 + 经 <see cref="IRealtimeBackplane"/> 广播给其他实例（多副本部署）。
/// </summary>
public interface IRealtimeNotifier
{
    /// <summary>推给指定用户（按 userCode）。</summary>
    /// <param name="userCode">用户唯一码（JWT sub claim 值）。</param>
    /// <param name="eventName">消息名（前端 connection.on 监听的方法名）。</param>
    /// <param name="payload">消息体（可空）。</param>
    /// <param name="traceId">追踪 ID（与审计日志打通）；null 时不附带。</param>
    /// <param name="cancellationToken"></param>
    Task NotifyUserAsync(string userCode, string eventName, object? payload = null, string? traceId = null, CancellationToken cancellationToken = default);

    /// <summary>推给指定组（如 <c>org:HQ</c> / <c>role:manager</c> / 业务自定义组）。</summary>
    Task NotifyGroupAsync(string groupName, string eventName, object? payload = null, string? traceId = null, CancellationToken cancellationToken = default);

    /// <summary>广播给所有已连接客户端。</summary>
    Task NotifyAllAsync(string eventName, object? payload = null, string? traceId = null, CancellationToken cancellationToken = default);
}
