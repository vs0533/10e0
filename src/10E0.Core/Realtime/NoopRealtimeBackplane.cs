using Microsoft.Extensions.Logging;

namespace TenE0.Core.Realtime;

/// <summary>
/// <see cref="IRealtimeBackplane"/> 的单体默认实现（#155）。
///
/// 不做任何跨实例广播 —— 消息仅在当前副本直推。单体 / 开发环境用此实现；
/// 多副本部署需替换为 Redis（或其它 pub/sub）backplane。
///
/// <see cref="Subscribe"/> 返回的 handler 永不被调用（无远端消息），
/// 返回一个 no-op <see cref="IDisposable"/>（订阅生命周期仍由调用方管理，保持接口对称）。
/// </summary>
public sealed class NoopRealtimeBackplane(ILogger<NoopRealtimeBackplane>? logger = null) : IRealtimeBackplane
{
    private readonly ILogger<NoopRealtimeBackplane>? _logger = logger;

    public Task PublishAsync(BackplaneMessage message, CancellationToken cancellationToken = default)
    {
        _logger?.LogDebug("Noop backplane: 跳过广播（单体模式）{Event}/{Delivery}", message.EventName, message.Delivery);
        return Task.CompletedTask;
    }

    public IDisposable Subscribe(Func<BackplaneMessage, Task> handler)
        => NoopDisposable.Instance;

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
