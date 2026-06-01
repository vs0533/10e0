using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TenE0.Core.Events.Outbox;

/// <summary>Outbox Relay 的运行参数。</summary>
public sealed class OutboxRelayOptions
{
    /// <summary>每次轮询取出的最大消息数。</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>两次轮询之间的间隔。空闲时建议长一些，繁忙时短一些。</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>单条消息的最大重试次数（达到后保留在表中并记录错误，不再尝试）。</summary>
    public int MaxAttempts { get; set; } = 8;
}

/// <summary>
/// Outbox 中继服务 — 把 OutboxMessage 表中未发送的事件持续投递出去。
///
/// 职责拆分：
/// - 本类只负责 "拉取批次 → 调投递器 → 更新状态 → 重试"
/// - 实际投递逻辑由 <see cref="IOutboxPublisher"/> 决定（进程内 / Kafka / CAP / RabbitMQ）
/// - 切换投递机制时，本类零改动
///
/// 失败处理：
/// - 抛异常即视为本次投递失败，AttemptCount + 1，LastError 记录原因
/// - 超过 MaxAttempts 后停止尝试（视为毒消息，需人工介入或 DLQ 工具处理）
/// - 单条消息失败不影响同批次其他消息（独立 try/catch）
/// </summary>
public sealed class OutboxRelayService<TContext>(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxRelayOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxRelayService<TContext>> logger) : BackgroundService
    where TContext : DbContext
{
    private readonly OutboxRelayOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("OutboxRelayService 启动：BatchSize={Batch}, Interval={Interval}",
            _options.BatchSize, _options.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken);
                if (processed == 0)
                    await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "OutboxRelayService 轮询发生异常，{Delay} 后重试", _options.PollInterval);
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
        }
    }

    private async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var dcFactory = sp.GetRequiredService<IDbContextFactory<TContext>>();
        var publisher = sp.GetRequiredService<IOutboxPublisher>();

        await using var dc = await dcFactory.CreateDbContextAsync(cancellationToken);
        var batch = await dc.Set<OutboxMessage>()
            .Where(m => m.SentTime == null && m.AttemptCount < _options.MaxAttempts)
            .OrderBy(m => m.OccurredOn)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        if (batch.Count == 0) return 0;

        foreach (var msg in batch)
        {
            msg.AttemptCount++;
            try
            {
                await publisher.PublishAsync(msg, cancellationToken);
                msg.SentTime = timeProvider.GetUtcNow();
                msg.LastError = null;
            }
            catch (Exception ex)
            {
                msg.LastError = Truncate(ex.Message, 2000);
                logger.LogWarning(ex,
                    "Outbox 消息投递失败 Id={Id} Attempt={Attempt}/{Max}",
                    msg.Id, msg.AttemptCount, _options.MaxAttempts);
            }
        }

        await dc.SaveChangesAsync(cancellationToken);
        return batch.Count;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max];
}
