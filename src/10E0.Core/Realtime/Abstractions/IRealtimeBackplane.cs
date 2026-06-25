namespace TenE0.Core.Realtime;

/// <summary>
/// 跨实例广播抽象（#155）。
///
/// 多副本部署时，一条消息只应被推一次给目标客户端 —— 但目标客户端可能连在任意一个副本上。
/// backplane 的职责：本副本发的消息先本地直推，再 <see cref="PublishAsync"/> 广播给其他副本；
/// 其他副本经 <see cref="Subscribe"/> 收到后本地直推（<b>不再回广播</b>，防回环）。
///
/// 单体部署用 <see cref="NoopRealtimeBackplane"/>（直推即可，无广播）。
/// Redis 实现留后续 issue（对齐 Outbox 的 lock 抽象）。
/// </summary>
public interface IRealtimeBackplane
{
    /// <summary>
    /// 广播一条推送给其他副本（本副本自己已直推，不在此处理）。
    /// 实现需保证至少 once 投递，幂等由消费端（<see cref="HubBasedRealtimeNotifier"/> 本地直推去重）兜底。
    /// </summary>
    Task PublishAsync(BackplaneMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// 订阅其他副本广播来的消息。<paramref name="handler"/> 被调用时应本地直推。
    /// 返回的 <see cref="IDisposable"/> 用于取消订阅（应用关闭时释放）。
    /// </summary>
    IDisposable Subscribe(Func<BackplaneMessage, Task> handler);
}

/// <summary>
/// backplane 传输的消息体。完全自包含 —— 任何副本收到都能直接本地直推，无需查 DB。
/// </summary>
public sealed record BackplaneMessage
{
    /// <summary>投放范围（决定本地走 Clients.User / Clients.Group / Clients.All）。</summary>
    public required NotificationTarget.Scope Delivery { get; init; }

    /// <summary>目标标识（userCode / 组名；All 范围忽略）。</summary>
    public string? Recipient { get; init; }

    /// <summary>消息名。</summary>
    public required string EventName { get; init; }

    /// <summary>已序列化的消息体（JSON 字符串）。</summary>
    public string? PayloadJson { get; init; }

    /// <summary>追踪 ID。</summary>
    public string? TraceId { get; init; }
}
