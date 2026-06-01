using Microsoft.EntityFrameworkCore;
using TenE0.Core.Sequences.Storage;

namespace TenE0.Core.Sequences;

/// <summary>
/// ISequenceGenerator 基于 EF Core 的实现。
///
/// 并发安全策略：
/// 1. 用乐观并发控制重试（捕获 DbUpdateConcurrencyException 自动重试）
/// 2. 同 sequenceKey 多请求并发取号时，最多被 DB 唯一索引拦截，重试即可
///
/// 为什么不用 SQL Server SEQUENCE？
/// - 多数据库 provider 兼容（SQL Server / Postgres / MySQL / SQLite / InMemory）
/// - 支持"日重置/月重置"这种 SQL SEQUENCE 不支持的语义
/// - 业务可读：DB 里直接看到 SequenceKey + CurrentBucket + CurrentNumber
///
/// 高并发场景下可加 RowLock（UPDATE ... WITH (UPDLOCK) on SQL Server）或换 SEQUENCE+前缀拼接。
/// </summary>
public sealed class EfSequenceGenerator<TContext>(
    IDbContextFactory<TContext> contextFactory,
    TimeProvider timeProvider) : ISequenceGenerator
    where TContext : DbContext
{
    private const int MaxRetries = 5;

    public async Task<string> NextAsync(string sequenceKey, string format, CancellationToken cancellationToken = default)
    {
        var parsed = SequenceFormat.Parse(format);
        var now = timeProvider.GetUtcNow();
        var bucket = SequenceFormat.RenderBucket(parsed, now);

        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                var number = await IncrementAsync(sequenceKey, bucket, cancellationToken);
                return SequenceFormat.Render(parsed, number, now);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxRetries - 1)
            {
                // 并发冲突，回退后重试
                await Task.Delay(Random.Shared.Next(5, 30), cancellationToken);
            }
            catch (DbUpdateException) when (attempt < MaxRetries - 1)
            {
                // 唯一索引冲突（首次插入并发场景），重试时另一个事务的记录已存在
                await Task.Delay(Random.Shared.Next(5, 30), cancellationToken);
            }
        }

        throw new InvalidOperationException($"流水号生成失败：序列 {sequenceKey} 多次重试仍未成功");
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

        // bucket 跨期：归零重置；同期：累加
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
