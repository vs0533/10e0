using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TenE0.Core.Sequences.Storage;

namespace TenE0.Core.Sequences;

/// <summary>
/// ISequenceGenerator 基于 EF Core 的实现。
///
/// 并发安全策略（#100 修复后）：
/// 1. <b>乐观并发控制</b> —— 通过 shadow property <c>RowVersion</c>（byte[] 时间戳/rowversion）
///    让 EF Core 在 UPDATE 时校验版本。并发 UPDATE 同一行时，第二个抛
///    <see cref="DbUpdateConcurrencyException"/> → 触发重试 → 重读最新值再 UPDATE。
///    这消除了旧实现的 <b>lost update</b>（SELECT+UPDATE 两往返间另一事务改了同一行，
///    但无并发 token 导致两个 UPDATE 都"成功"，其中一个的更新被覆盖）。
/// 2. <b>唯一索引兜底</b> —— <c>SequenceKey</c> 唯一索引让首次插入并发只一个成功，另一个重试时走 UPDATE。
/// 3. <b>重试耗尽带上下文</b>（#100 问题 1）—— 5 次重试后抛带 key/bucket/最后异常的 <see cref="InvalidOperationException"/>，
///    而非裸 "流水号生成失败"，便于运维定位。
///
/// 为什么不用 SQL Server SEQUENCE？
/// - 多数据库 provider 兼容（SQL Server / Postgres / MySQL / SQLite / InMemory）
/// - 支持"日重置/月重置"这种 SQL SEQUENCE 不支持的语义
/// - 业务可读：DB 里直接看到 SequenceKey + CurrentBucket + CurrentNumber
/// </summary>
public sealed class EfSequenceGenerator<TContext>(
    IDbContextFactory<TContext> contextFactory,
    TimeProvider timeProvider,
    ILogger<EfSequenceGenerator<TContext>>? logger = null) : ISequenceGenerator
    where TContext : DbContext
{
    private const int MaxRetries = 5;

    public async Task<string> NextAsync(string sequenceKey, string format, CancellationToken cancellationToken = default)
    {
        var parsed = SequenceFormat.Parse(format);
        var now = timeProvider.GetUtcNow();
        var bucket = SequenceFormat.RenderBucket(parsed, now);

        Exception? lastError = null;
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                var number = await IncrementAsync(sequenceKey, bucket, cancellationToken);
                return SequenceFormat.Render(parsed, number, now);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // #100: 乐观并发冲突（RowVersion 不匹配）。前 MaxRetries-1 次回退重试，
                // 最后一次（attempt == MaxRetries-1）也捕获以落入下方"重试耗尽"带上下文抛出。
                lastError = ex;
                if (attempt < MaxRetries - 1)
                    await Task.Delay(Random.Shared.Next(5, 30), cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                // 唯一索引冲突（首次插入并发场景），重试时另一个事务的记录已存在。
                lastError = ex;
                if (attempt < MaxRetries - 1)
                    await Task.Delay(Random.Shared.Next(5, 30), cancellationToken);
            }
        }

        // #100 问题 1：重试耗尽时记录完整上下文（key/bucket/最后错误），而非裸异常。
        logger?.LogError(lastError,
            "流水号生成重试 {MaxRetries} 次仍失败：SequenceKey={Key} Bucket={Bucket}",
            MaxRetries, sequenceKey, bucket);
        throw new InvalidOperationException(
            $"流水号生成失败：序列 '{sequenceKey}'（bucket='{bucket}'）重试 {MaxRetries} 次仍冲突。" +
            "常见原因：极高并发取号或数据库死锁。最后错误见 InnerException / 日志。", lastError);
    }

    private async Task<long> IncrementAsync(string sequenceKey, string bucket, CancellationToken cancellationToken)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await ctx.Set<TenE0Sequence>()
            .FirstOrDefaultAsync(s => s.SequenceKey == sequenceKey, cancellationToken);

        if (existing is null)
        {
            // 首次取号
            var seed = new TenE0Sequence
            {
                SequenceKey = sequenceKey,
                CurrentBucket = bucket,
                CurrentNumber = 1,
            };
            ctx.Set<TenE0Sequence>().Add(seed);
            await ctx.SaveChangesAsync(cancellationToken);
            return 1;
        }

        // bucket 跨期：归零重置；同期：累加。
        // #100: RowVersion shadow property 让 SaveChangesAsync 在 UPDATE 时校验版本 ——
        // 若并发事务已改本行，抛 DbUpdateConcurrencyException 触发上层重试，避免 lost update。
        if (existing.CurrentBucket != bucket)
        {
            existing.CurrentBucket = bucket;
            existing.CurrentNumber = 1;
        }
        else
        {
            existing.CurrentNumber += 1;
        }

        await ctx.SaveChangesAsync(cancellationToken);
        return existing.CurrentNumber;
    }
}
