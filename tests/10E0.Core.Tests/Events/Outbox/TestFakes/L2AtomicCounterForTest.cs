using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using TenE0.Core.Caching;

namespace TenE0.Core.Tests.Events.Outbox.TestFakes;

/// <summary>
/// 单进程 <see cref="IAtomicCounter"/> fake —— 复刻 <c>DefaultCachingImplementations.DistributedAtomicCounter</c>
/// 行为但完全单进程可控。
///
/// <para>
/// 用于 #82 Leader 模式测试：让 <see cref="LeaderElector"/> 的 lease 续约路径有真实可断言的语义。
/// 注意：本 fake 的 IncrementAsync 用 read-then-write 不是真原子（与生产 DistributedAtomicCounter 同款限制），
/// 多进程部署需 Redis 等真原子 INCR 替换。
/// </para>
/// </summary>
public sealed class L2AtomicCounterForTest : IAtomicCounter
{
    private readonly IDistributedCache _cache;

    public L2AtomicCounterForTest(IDistributedCache cache) => _cache = cache;

    public async Task<long> IncrementAsync(string key, CancellationToken cancellationToken = default)
    {
        var raw = await _cache.GetAsync(key, cancellationToken);
        var current = raw is { Length: > 0 } &&
                      long.TryParse(Encoding.UTF8.GetString(raw), out var n)
            ? n
            : 0L;
        var next = current + 1L;
        await _cache.SetAsync(
            key,
            Encoding.UTF8.GetBytes(next.ToString()),
            new DistributedCacheEntryOptions(),
            cancellationToken);
        return next;
    }

    public async Task<long> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var raw = await _cache.GetAsync(key, cancellationToken);
        return raw is { Length: > 0 } &&
               long.TryParse(Encoding.UTF8.GetString(raw), out var n)
            ? n
            : 0L;
    }
}
