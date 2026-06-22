using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using TenE0.Core.Caching;

namespace TenE0.Core.Tests.Events.Outbox.TestFakes;

/// <summary>
/// Outbox 应用层锁测试专用的 <see cref="IMultiLevelCache"/> fake —— 与生产
/// <c>DefaultCachingImplementations.MultiLevelCache</c> 行为一致但完全单进程可控。
///
/// <para>
/// 用于 #82 Distributed/Leader 模式的单进程单元/验收测试：让 <see cref="DistributedOutboxLock"/>
/// 和 <see cref="LeaderElector"/> 的 SETNX / 续约路径有真实可断言的语义，而不是依赖 Redis。
/// </para>
///
/// <para>
/// <b>原子性限制</b>：本 fake 是单进程的，<see cref="TrySetAsync"/> 内部的
/// L1 TryGetValue + L2 GetAsync 之间的非原子窗口只对单进程内的多线程有意义；
/// 生产 Redis 部署应替换实现为真 SETNX。本 fake 的 SETNX 语义对单进程测试足够。
/// </para>
/// </summary>
public sealed class L1L2CacheForTest : IMultiLevelCache
{
    private readonly IMemoryCache _l1;
    private readonly IDistributedCache _l2;

    public L1L2CacheForTest(IMemoryCache l1, IDistributedCache l2)
    {
        _l1 = l1;
        _l2 = l2;
    }

    public async Task<T?> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T?>> factory,
        CacheOptions options,
        CancellationToken cancellationToken = default)
        where T : class
    {
        if (_l1.TryGetValue(key, out var l1Hit) && l1Hit is T l1Typed)
            return l1Typed;

        var l2Bytes = await _l2.GetAsync(key, cancellationToken);
        if (l2Bytes is { Length: > 0 })
        {
            var fromL2 = JsonSerializer.Deserialize<T>(l2Bytes);
            if (fromL2 is not null)
            {
                SetL1(key, fromL2, options);
                return fromL2;
            }
        }

        var fresh = await factory(cancellationToken);
        if (fresh is null) return null;
        await SetL2Async(key, fresh, options, cancellationToken);
        SetL1(key, fresh, options);
        return fresh;
    }

    public async Task<bool> TrySetAsync<T>(
        string key,
        T value,
        CacheOptions options,
        CancellationToken cancellationToken = default)
        where T : class
    {
        if (_l1.TryGetValue(key, out _)) return false;
        var existing = await _l2.GetAsync(key, cancellationToken);
        if (existing is { Length: > 0 }) return false;
        await SetL2Async(key, value, options, cancellationToken);
        SetL1(key, value, options);
        return true;
    }

    public async Task SetAsync<T>(
        string key,
        T value,
        CacheOptions options,
        CancellationToken cancellationToken = default)
        where T : class
    {
        await SetL2Async(key, value, options, cancellationToken);
        SetL1(key, value, options);
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _l1.Remove(key);
        await _l2.RemoveAsync(key, cancellationToken);
    }

    private void SetL1<T>(string key, T value, CacheOptions options) where T : class
    {
        if (options.L1Duration <= TimeSpan.Zero) return;
        _l1.Set(key, value, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = options.L1Duration,
        });
    }

    private async Task SetL2Async<T>(
        string key,
        T value,
        CacheOptions options,
        CancellationToken cancellationToken) where T : class
    {
        if (options.L2Duration <= TimeSpan.Zero) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        await _l2.SetAsync(key, bytes,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = options.L2Duration },
            cancellationToken);
    }
}
