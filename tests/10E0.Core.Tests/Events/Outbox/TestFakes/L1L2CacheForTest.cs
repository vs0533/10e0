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
        // 用真原子的 TryAdd 代替"先 Get 再 Set"序列 —— 避免 TOCTOU race。
        // 早期实现 (PR #88 早期) 用 GetAsync + SetAsync 序列，hostA 跑到 Set 之前 hostB 也 Get 完成 → 两个都 success → 都 publish。
        // 生产 Redis SETNX 天然原子，fake 必须用锁/CompareExchange 才能测出 SETNX 行为正确性。
        //（同进程内多线程有效；跨进程需要真 Redis SETNX 才能验证 —— fake 的根本限制。）
        if (options.L2Duration <= TimeSpan.Zero) return false;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        var distributedOptions = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = options.L2Duration,
        };

        // L1 命中直接 false（沿用原语义：L1 看作 L2 的子集，有就一定存在）
        if (_l1.TryGetValue(key, out _)) return false;

        // 关键：调用 L2 的原子 TryAdd —— 不存在时才写，存在时直接 false，避免 hostA 写完 hostB 又覆盖
        bool added;
        if (_l2 is InMemoryDistributedCache inMem)
        {
            added = inMem.TryAdd(key, bytes, distributedOptions);
        }
        else
        {
            // 兜底：非 InMemory 实现走 read-then-write（仍可能有 TOCTOU，但 IDistributedCache 标准接口没 TryAdd）
            var existing = await _l2.GetAsync(key, cancellationToken);
            if (existing is { Length: > 0 }) return false;
            await _l2.SetAsync(key, bytes, distributedOptions, cancellationToken);
            added = true;
        }

        if (!added) return false;

        // L1 同步 set（沿用原语义：L2 成功写后才 set L1，让 L1 与 L2 状态一致）
        if (options.L1Duration > TimeSpan.Zero)
        {
            _l1.Set(key, value, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = options.L1Duration,
            });
        }
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
