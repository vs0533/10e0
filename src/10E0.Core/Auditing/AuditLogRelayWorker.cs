using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TenE0.Core.Auditing;

/// <summary>
/// 审计日志后台落库 Worker —— 从 <see cref="AuditLogChannel"/> 批量读取条目，
/// 用独立 scope 的 <c>IDbContextFactory</c> 批量写库。
///
/// <para>
/// <b>设计要点（对齐 <c>OutboxRelayService</c> 模式）：</b>
/// <list type="bullet">
/// <item>独立 DbContext + 独立事务：审计写入与业务事务完全隔离，审计失败永不回滚业务。</item>
/// <item>best-effort：写入失败只记日志，条目丢弃不重试（重试需持久化队列，本期不做 ——
///   审计丢失可接受，业务正确性不可妥协）。</item>
/// <item>优雅停机：收到停止信号后 drain Channel 剩余条目，最多等 <see cref="AuditOptions.DrainTimeout"/>。</item>
/// </list>
/// </para>
/// <para>
/// <b>为何用 <c>AddHostedService</c> 而非 <c>IHostedLifecycleService</c>：</c>审计 worker 不需要
/// 在端口监听前就绪（审计可丢），普通 BackgroundService 的 StartAsync 时序足够。
/// </para>
/// </summary>
public sealed class AuditLogRelayWorker<TContext>(
    AuditLogChannel channel,
    IServiceScopeFactory scopeFactory,
    IOptions<AuditOptions> options,
    ILogger<AuditLogRelayWorker<TContext>> logger) : BackgroundService
    where TContext : DbContext
{
    private readonly AuditOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "AuditLogRelayWorker 启动：BatchSize={Batch}, PollInterval={Interval}",
            _options.BatchSize, _options.PollInterval);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // 非阻塞处理一批；空时阻塞等待新数据或取消（WaitToReadAsync 受 stoppingToken 取消）。
                var processed = await ProcessBatchAsync(stoppingToken);
                if (processed == 0)
                {
                    await channel.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { /* 正常停机 */ }
        catch (Exception ex)
        {
            logger.LogError(ex, "AuditLogRelayWorker 主循环异常");
        }

        // ---- 优雅停机：drain 剩余条目 ----
        await DrainAsync();
    }

    /// <summary>
    /// 读一批条目并落库（非阻塞：Channel 空时立即返回 0）。
    /// 标记 internal 供测试直接调用（对齐 OutboxRelayService.ProcessBatchAsync）。
    /// 阻塞等待逻辑在 <see cref="ExecuteAsync"/> 中，本方法只负责"有则处理、无则返回"。
    /// </summary>
    internal async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        // 纯非阻塞 TryRead 凑批：凑满 BatchSize 或 Channel 暂时无数据即提交。
        var batch = new List<AuditChannelItem>(_options.BatchSize);
        while (batch.Count < _options.BatchSize)
        {
            if (!channel.Reader.TryRead(out var item)) break;
            batch.Add(item);
        }

        if (batch.Count == 0) return 0;

        await PersistAsync(batch, cancellationToken);
        return batch.Count;
    }

    /// <summary>把一批条目写入数据库（独立 scope + 独立 DbContext）。</summary>
    private async Task PersistAsync(List<AuditChannelItem> batch, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dcFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TContext>>();
            await using var dc = await dcFactory.CreateDbContextAsync(cancellationToken);

            // CreateTime 已在 Sink 入队时盖好（统一时间源），这里只做条目 → 实体映射。
            foreach (var item in batch)
            {
                switch (item)
                {
                    case AuditChannelItem.Op op:
                        dc.Set<TenE0AuditLog>().Add(ToEntity(op.Entry));
                        break;
                    case AuditChannelItem.Login login:
                        dc.Set<TenE0LoginLog>().Add(ToEntity(login.Entry));
                        break;
                }
            }
            await dc.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // best-effort：整批失败直接丢弃，只记日志。不重试、不阻塞 worker。
            logger.LogWarning(ex,
                "审计日志批量落库失败，丢弃 {Count} 条条目（best-effort 契约）",
                batch.Count);
        }
    }

    /// <summary>停机时 drain Channel 剩余条目，最多等 <see cref="AuditOptions.DrainTimeout"/>。</summary>
    private async Task DrainAsync()
    {
        // 标记 Channel 写端完成：Reader 能读到剩余条目后正常结束。
        channel.Complete();

        using var cts = new CancellationTokenSource(_options.DrainTimeout);
        var drained = 0;
        try
        {
            // 持续读直到 Channel 完全空（Reader.Completion）或 drain 超时。
            await foreach (var item in channel.Reader.ReadAllAsync(cts.Token))
            {
                drained++;
                // 单条落库（停机期量小，不再凑批）
                await PersistAsync([item], CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("审计日志 drain 超时（{Timeout}），剩余条目丢失",
                _options.DrainTimeout);
        }

        if (drained > 0)
            logger.LogInformation("审计日志停机 drain 完成：落库 {Count} 条", drained);
    }

    private static TenE0AuditLog ToEntity(AuditLogEntry e) => new()
    {
        TraceId = e.TraceId,
        ActorType = e.ActorType,
        ActorCode = e.ActorCode,
        EntityType = e.EntityType,
        EntityId = e.EntityId,
        Action = e.Action,
        ChangedFieldsJson = e.ChangedFieldsJson,
        IpAddress = e.IpAddress,
        UserAgent = e.UserAgent,
        CreateTime = e.CreateTime,
    };

    private static TenE0LoginLog ToEntity(LoginLogEntry e) => new()
    {
        UserCode = e.UserCode,
        EventType = e.EventType,
        Success = e.Success,
        IpAddress = e.IpAddress,
        UserAgent = e.UserAgent,
        FailureReason = e.FailureReason,
        ExpiresAt = e.ExpiresAt,
        CreateTime = e.CreateTime,
    };
}
