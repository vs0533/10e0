using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TenE0.Core.Realtime;

/// <summary>
/// backplane 订阅宿主服务（#155）—— 应用启动时订阅一次，远端副本广播来的消息经此本地直推。
///
/// 为何独立成 IHostedService：<see cref="HubBasedRealtimeNotifier"/> 是 Scoped（每次推送新建作用域），
/// 而 backplane 订阅必须在应用生命周期内持续有效（一次订阅），不能随 scope 释放。
/// 本类为 Singleton 生命周期，<see cref="IHubContext{THub}"/> 与 <see cref="IRealtimeBackplane"/>
/// 均可安全注入，订阅句柄在 <see cref="StopAsync"/> 释放。
///
/// 防回环：远端消息本地直推时 <b>不再</b> 调 <see cref="IRealtimeBackplane.PublishAsync"/>
/// （否则会在副本间无限反弹）。
/// </summary>
public sealed class RealtimeBackplaneSubscriber(
    IHubContext<NotificationHub> hubContext,
    IRealtimeBackplane backplane,
    ILogger<RealtimeBackplaneSubscriber> logger) : IHostedService
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = backplane.Subscribe(OnRemoteMessageAsync);
        logger.LogInformation("Realtime backplane 订阅已建立：{Backplane}", backplane.GetType().Name);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        logger.LogInformation("Realtime backplane 订阅已释放");
        return Task.CompletedTask;
    }

    /// <summary>处理远端副本广播来的消息 —— 本地直推，不再回广播。</summary>
    private async Task OnRemoteMessageAsync(BackplaneMessage message)
    {
        try
        {
            var envelope = new NotificationEnvelope(message.EventName, message.PayloadJson, message.TraceId);
            switch (message.Delivery)
            {
                case NotificationTarget.Scope.User:
                    await hubContext.Clients.User(message.Recipient!).SendAsync(message.EventName, envelope);
                    break;
                case NotificationTarget.Scope.Group:
                    await hubContext.Clients.Group(message.Recipient!).SendAsync(message.EventName, envelope);
                    break;
                default: // All
                    await hubContext.Clients.All.SendAsync(message.EventName, envelope);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理远端 backplane 消息失败 {Event}/{Delivery}/{Recipient}",
                message.EventName, message.Delivery, message.Recipient);
        }
    }
}
