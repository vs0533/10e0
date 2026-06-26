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
    private readonly ConcurrentDictionary<string, object> _locks = new();

    /// <inheritdoc />
    public Task<LoginAttemptState> GetAsync(string userCode, CancellationToken ct = default)
    {
        var snapshot = ReadSnapshot(userCode, TimeProvider.System);
        return Task.FromResult(snapshot);
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
        lock (LockFor(userCode))
        {
            var now = timeProvider.GetUtcNow();
            var current = _store.GetValueOrDefault(userCode);

            // 滑动窗口：上次失败距今超过窗口，重置计数起点
            if (current is not null && (now - current.LastFailureAt) > slidingWindow)
            {
                current = null;
            }

            var newCount = (current?.FailedCount ?? 0) + 1;
            DateTimeOffset? lockedUntil = current?.LockedUntil;

            // 达到阈值 → 写锁定截止时间（除非已锁更长）
            if (newCount >= maxFailedAttempts)
            {
                var proposedLock = now + lockoutDuration;
                lockedUntil = lockedUntil is { } existing && existing > proposedLock
                    ? existing
                    : proposedLock;
            }

            _store[userCode] = new Entry(newCount, now, lockedUntil);
            return Task.FromResult(new LoginAttemptState(newCount, lockedUntil));
        }
    }

    /// <inheritdoc />
    public Task RecordSuccessAsync(string userCode, CancellationToken ct = default)
    {
        lock (LockFor(userCode))
        {
            _store.TryRemove(userCode, out _);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ResetAsync(string userCode, CancellationToken ct = default)
    {
        lock (LockFor(userCode))
        {
            _store.TryRemove(userCode, out _);
        }
        return Task.CompletedTask;
    }

    private LoginAttemptState ReadSnapshot(string userCode, TimeProvider timeProvider)
    {
        // 读不取锁（ConcurrentDictionary 读线程安全）；返回 entry 当前快照
        if (_store.TryGetValue(userCode, out var entry))
        {
            // 已过期的锁视为未锁：调用方判定，但这里返回原始 LockedUntil 让上层决定是否清。
            return new LoginAttemptState(entry.FailedCount, entry.LockedUntil);
        }
        return new LoginAttemptState(0, null);
    }

    private object LockFor(string userCode) => _locks.GetOrAdd(userCode, _ => new object());

    private sealed record Entry(int FailedCount, DateTimeOffset LastFailureAt, DateTimeOffset? LockedUntil);
}
