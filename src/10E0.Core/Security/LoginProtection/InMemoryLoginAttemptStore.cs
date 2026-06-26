using System.Collections.Concurrent;

namespace TenE0.Core.Security.LoginProtection;

/// <summary>
/// 进程内 <see cref="ILoginAttemptStore"/> 默认实现（issue #162）。
///
/// <para>
/// 单实例部署足够。多副本部署必须 Replace 为分布式实现（Redis <c>INCR</c>），
/// 否则每个副本独立计数，撞库阈值翻倍绕过。注释中明确标注此约束，对应 #98 同款警告风格。
/// </para>
///
/// <para><b>并发模型</b>：每 userCode 一把 <see cref="ConcurrentDictionary"/> 锁，保证 read-modify-write
/// 原子；不同用户不互斥。</para>
/// </summary>
public sealed class InMemoryLoginAttemptStore : ILoginAttemptStore
{
    /// <summary>
    /// 多副本并发 race 警告关键字（与 <c>DistributedAtomicCounter</c> 同风格，测试可断言命中）：
    /// 单机内存存储在多副本部署下计数失真，必须 Replace 为 Redis <c>INCR</c> 实现。
    /// </summary>
    internal static readonly string[] MultiReplicaRaceWarningKeywords =
    {
        "多副本",
        "Replace",
        "INCR",
    };

    private readonly ConcurrentDictionary<string, Entry> _store = new();

    /// <inheritdoc />
    public Task<LoginAttemptState> GetAsync(string userCode, CancellationToken ct = default)
    {
        // 读不取锁（ConcurrentDictionary 读线程安全）；返回 entry 当前快照
        if (_store.TryGetValue(userCode, out var entry))
        {
            return Task.FromResult(new LoginAttemptState(entry.FailedCount, entry.LockedUntil));
        }
        return Task.FromResult(new LoginAttemptState(0, null));
    }

    /// <inheritdoc />
    public Task<LoginAttemptState> RecordFailureAsync(
        string userCode,
        int maxFailedAttempts,
        TimeSpan slidingWindow,
        TimeSpan lockoutDuration,
        TimeProvider timeProvider,
        CancellationToken ct = default)
    {
        // 用 entry 自带的 lock 对象保证 read-modify-write 原子性（per-user 锁）。
        // #162 review #5：lock 对象与 Entry 合并存储，entry 被清除时 lock 也随之 GC，
        // 避免用户名枚举攻击让 _locks 字典无界增长。
        var entry = _store.GetOrAdd(userCode, _ => new Entry());
        lock (entry)
        {
            var now = timeProvider.GetUtcNow();
            var current = entry;

            // 滑动窗口：上次失败距今超过窗口，重置计数起点
            if ((now - entry.LastFailureAt) > slidingWindow)
            {
                entry.FailedCount = 0;
            }

            var newCount = entry.FailedCount + 1;
            DateTimeOffset? lockedUntil = entry.LockedUntil;

            // 达到阈值 → 写锁定截止时间（除非已锁更长）
            if (newCount >= maxFailedAttempts)
            {
                var proposedLock = now + lockoutDuration;
                lockedUntil = lockedUntil is { } existing && existing > proposedLock
                    ? existing
                    : proposedLock;
            }

            entry.FailedCount = newCount;
            entry.LastFailureAt = now;
            entry.LockedUntil = lockedUntil;
            return Task.FromResult(new LoginAttemptState(newCount, lockedUntil));
        }
    }

    /// <inheritdoc />
    public Task RecordSuccessAsync(string userCode, CancellationToken ct = default)
    {
        _store.TryRemove(userCode, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ResetAsync(string userCode, CancellationToken ct = default)
    {
        _store.TryRemove(userCode, out _);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 持久化条目：失败计数 + 最后失败时间 + 锁定截止时间 + per-entry 同步锁。
    /// lock 内联在 entry 上，entry 被 <see cref="RecordSuccessAsync"/> / <see cref="ResetAsync"/>
    /// 清除时 lock 对象一并 GC，杜绝用户名枚举导致的内存膨胀。
    /// </summary>
    private sealed class Entry
    {
        public int FailedCount;
        public DateTimeOffset LastFailureAt;
        public DateTimeOffset? LockedUntil;
    }
}
