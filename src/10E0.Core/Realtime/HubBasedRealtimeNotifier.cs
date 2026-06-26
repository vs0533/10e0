using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace TenE0.Core.Realtime;

/// <summary>
/// <see cref="IRealtimeNotifier"/> 默认实现（#155）—— 持 <see cref="IHubContext{THub}"/> 直推 + backplane 广播。
///
/// 投递流程（以 NotifyUserAsync 为例）：
/// <list type="number">
/// <item>本地直推：当前副本的连接经 <c>Clients.User(code).SendAsync(eventName, envelope)</c> 投递。</item>
/// <item>跨实例广播：经 <see cref="IRealtimeBackplane.PublishAsync"/> 把消息发给其他副本
/// （本副本已直推，不在此重复）。其他副本经 Subscribe 回调本地直推（不再回广播，防回环）。</item>
/// </list>
///
/// 消息信封 <see cref="NotificationEnvelope"/>：固定结构（eventName + payload + traceId），
/// 前端 <c>connection.on</c> 收到的参数就是它 —— 便于前端统一解析 traceId / 做日志关联。
///
/// 生命周期：Scoped（<see cref="IHubContext{THub}"/> 注入安全；backplane 订阅在单例 host 启动时建立一次）。
/// </summary>
public sealed class HubBasedRealtimeNotifier(
    IHubContext<NotificationHub> hubContext,
    IRealtimeBackplane backplane,
    ILogger<HubBasedRealtimeNotifier> logger) : IRealtimeNotifier
{
    public Task NotifyUserAsync(string userCode, string eventName, object? payload = null, string? traceId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        traceId ??= ResolveTraceId();
        return DeliverAsync(
            local: () => hubContext.Clients.User(userCode).SendAsync(eventName, new NotificationEnvelope(eventName, payload, traceId), cancellationToken),
            delivery: NotificationTarget.Scope.User,
            recipient: userCode,
            eventName: eventName,
            payload: payload,
            traceId: traceId);
    }

    public Task NotifyGroupAsync(string groupName, string eventName, object? payload = null, string? traceId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        traceId ??= ResolveTraceId();
        return DeliverAsync(
            local: () => hubContext.Clients.Group(groupName).SendAsync(eventName, new NotificationEnvelope(eventName, payload, traceId), cancellationToken),
            delivery: NotificationTarget.Scope.Group,
            recipient: groupName,
            eventName: eventName,
            payload: payload,
            traceId: traceId);
    }

    public Task NotifyAllAsync(string eventName, object? payload = null, string? traceId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        traceId ??= ResolveTraceId();
        return DeliverAsync(
            local: () => hubContext.Clients.All.SendAsync(eventName, new NotificationEnvelope(eventName, payload, traceId), cancellationToken),
            delivery: NotificationTarget.Scope.All,
            recipient: null,
            eventName: eventName,
            payload: payload,
            traceId: traceId);
    }

    /// <summary>本地直推 + backplane 广播（二者并行，互不阻塞；任一失败仅记日志不抛 —— 推送是 best-effort）。</summary>
    private async Task DeliverAsync(
        Func<Task> local,
        NotificationTarget.Scope delivery,
        string? recipient,
        string eventName,
        object? payload,
        string? traceId)
    {
        // 本地直推
        try
        {
            await local();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "本地直推失败 {Event}/{Delivery}/{Recipient}", eventName, delivery, recipient);
        }

        // 跨实例广播（本副本已直推，此消息给其他副本；Noop backplane 是空操作）
        try
        {
            await backplane.PublishAsync(new BackplaneMessage
            {
                Delivery = delivery,
                Recipient = recipient,
                EventName = eventName,
                PayloadJson = payload is null ? null : JsonSerializer.Serialize(payload),
                TraceId = traceId,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "backplane 广播失败 {Event}/{Delivery}/{Recipient}", eventName, delivery, recipient);
        }
    }

    private static string? ResolveTraceId() => System.Diagnostics.Activity.Current?.TraceId.ToString();
}

/// <summary>
/// 推送给前端的消息信封。前端 <c>connection.on(eventName, envelope =&gt; ...)</c> 收到此对象，
/// <see cref="TraceId"/> 可与审计日志 / APM 关联排障。<see cref="Data"/> 即原 payload（SignalR 端 JSON 序列化）。
/// </summary>
public sealed record NotificationEnvelope(string Event, object? Data, string? TraceId);
