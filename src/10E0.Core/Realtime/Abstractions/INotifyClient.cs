using System.Text.Json;
using TenE0.Core.Events;

namespace TenE0.Core.Realtime;

/// <summary>
/// 声明式实时推送的标记契约（#155）。
///
/// 任何领域事件实现本接口，框架的 <see cref="NotificationDispatcher"/> 即自动把它推送给前端 ——
/// 业务方零样板：无需手写 handler、无需注入 <see cref="IRealtimeNotifier"/>、无需关心 Hub / backplane。
///
/// 用法：
/// <code>
/// public sealed record OrderApprovedEvent(
///     string OrderId, string ApproverCode)
///     : INotifyClient
/// {
///     public NotificationTarget Target => NotificationTarget.User(ApproverCode, "order.approved", new { OrderId });
/// }
/// </code>
/// 该事件一旦经 <c>IDomainEventDispatcher.DispatchAsync</c> 派发，<c>ApproverCode</c> 的客户端就会收到
/// <c>order.approved</c> 消息。
///
/// 设计：本接口同时是 <see cref="IDomainEvent"/>（可进 Outbox / 进程内分发），<see cref="Target"/>
/// 由实现提供（通常用 record 的表达式体，零状态字段），<see cref="NotificationDispatcher"/> 据此决定推 user / group / all。
/// </summary>
public interface INotifyClient : IDomainEvent
{
    /// <summary>
    /// 推送目标（推给谁、事件名、payload）。
    /// 实现通常返回 <see cref="NotificationTarget"/> 的静态工厂（User / Group / All），值随事件字段。
    /// </summary>
    NotificationTarget Target { get; }
}

/// <summary>
/// 一次实时推送的目标描述。不可变。
///
/// 三种投放范围：
/// <list type="bullet">
/// <item><see cref="Scope.User"/>：推给指定用户（按 JWT <c>sub</c> claim 值，即 userCode）—— 走 <c>Clients.User(code)</c>。</item>
/// <item><see cref="Scope.Group"/>：推给指定组（如 <c>org:HQ</c> / <c>role:manager</c> / 业务自定义）—— 走 <c>Clients.Group(name)</c>。</item>
/// <item><see cref="Scope.All"/>：广播给所有已连接客户端 —— 走 <c>Clients.All</c>。</item>
/// </list>
///
/// <see cref="Payload"/> 序列化为 JSON 随消息体下发。事件名 <see cref="EventName"/> 即前端 <c>connection.on(eventName, ...)</c> 监听的方法名。
/// </summary>
public sealed record NotificationTarget
{
    /// <summary>推送范围。</summary>
    public enum Scope { User, Group, All }

    /// <summary>消息名（前端 connection.on 监听的方法名）。如 <c>order.approved</c>。</summary>
    public required string EventName { get; init; }

    /// <summary>推送范围。</summary>
    public required Scope Delivery { get; init; }

    /// <summary>
    /// 目标标识。User 范围下是 userCode；Group 范围下是组名（如 <c>org:HQ</c>）；All 范围下忽略。
    /// </summary>
    public string? Recipient { get; init; }

    /// <summary>消息体（JSON 序列化后随 WebSocket 下发）。可空。</summary>
    public object? Payload { get; init; }

    /// <summary>关联的追踪 ID（与审计日志 / Outbox 打通，便于跨系统排障）。缺省自动取 <c>Activity.Current.TraceId</c>。</summary>
    public string? TraceId { get; init; }

    /// <summary>推给指定用户。</summary>
    /// <param name="userCode">用户唯一码（JWT sub claim 值）。</param>
    /// <param name="eventName">消息名。</param>
    /// <param name="payload">消息体。</param>
    /// <param name="traceId">追踪 ID；null 时由 dispatcher 自动填充。</param>
    public static NotificationTarget User(string userCode, string eventName, object? payload = null, string? traceId = null) =>
        new() { EventName = eventName, Delivery = Scope.User, Recipient = userCode, Payload = payload, TraceId = traceId };

    /// <summary>推给指定组（如 <c>org:HQ</c> / <c>role:manager</c>）。</summary>
    public static NotificationTarget Group(string groupName, string eventName, object? payload = null, string? traceId = null) =>
        new() { EventName = eventName, Delivery = Scope.Group, Recipient = groupName, Payload = payload, TraceId = traceId };

    /// <summary>广播给所有已连接客户端。</summary>
    public static NotificationTarget All(string eventName, object? payload = null, string? traceId = null) =>
        new() { EventName = eventName, Delivery = Scope.All, Payload = payload, TraceId = traceId };

    /// <summary>把 Payload 序列化为 JSON 字符串（推送时用，避免 Hub 侧重复序列化 + 保持类型中立）。</summary>
    public string? PayloadJson => Payload is null ? null : JsonSerializer.Serialize(Payload);
}
