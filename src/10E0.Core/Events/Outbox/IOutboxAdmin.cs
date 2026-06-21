namespace TenE0.Core.Events.Outbox;

/// <summary>
/// Outbox 死信（Poison Message）管理契约 — 供运维/排障消费者解耦具体 DbContext 类型。
///
/// <para>
/// Poison 判定语义：<c>SentTime IS NULL AND AttemptCount &gt;= MaxAttempts</c>，
/// 与 <see cref="OutboxRelayService"/> 的 <c>ProcessBatchAsync</c> 过滤条件对偶
/// （Relay 只取 <c>SentTime IS NULL AND AttemptCount &lt; MaxAttempts</c>，
/// 未被取走且累计超阈的即为毒消息）。
/// </para>
///
/// <para>
/// 重试（<see cref="RetryPoisonMessageAsync"/>）会清零 AttemptCount 并清空 LastError，
/// SentTime 保持 null — 下轮 Relay 会重新拾取并尝试投递。
/// </para>
///
/// <para>
/// 阈值 <c>MaxAttempts</c> 必须复用 <see cref="OutboxRelayOptions.MaxAttempts"/>，
/// 实现层禁止自行定义或硬编码，避免与 Relay 配置漂移。
/// </para>
/// </summary>
public interface IOutboxAdmin
{
    /// <summary>
    /// 查询当前所有毒消息（未发送且尝试次数已耗尽）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>毒消息实体列表，按 <see cref="OutboxMessage.OccurredOn"/> 升序；空表返回空集合。</returns>
    Task<IReadOnlyList<OutboxMessage>> GetPoisonMessagesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 手动重置单条毒消息，让 Relay 下轮重新拾取。
    /// </summary>
    /// <param name="id">OutboxMessage 主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// <c>true</c> 表示找到并重置成功；<c>false</c> 表示 ID 不存在（不抛异常，运维脚本可幂等轮询）。
    /// </returns>
    Task<bool> RetryPoisonMessageAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// 导出毒消息为结构化 DTO（排障关键字段），便于离线分析（CSV/JSON）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>DTO 列表，字段集合与 <see cref="OutboxPoisonMessageDto"/> 定义一致。</returns>
    Task<IReadOnlyList<OutboxPoisonMessageDto>> ExportPoisonMessagesAsync(CancellationToken cancellationToken);
}
