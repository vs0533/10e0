using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace TenE0.Core.Security.Captcha;

/// <summary>
/// 验证码缓存条目（落 <see cref="IDistributedCache"/>，序列化为 JSON 字节）。
/// </summary>
/// <param name="Answer">答案；空字符串表示"已消费"哨兵（review #7 防重放）。</param>
internal sealed record CaptchaEntry(string Answer);

/// <summary>
/// 验证码存储抽象 + 默认 <see cref="IDistributedCache"/> 实现。
///
/// <para>
/// 仅用 <see cref="IDistributedCache"/>（不另开 L1 <c>IMemoryCache</c>）—— 验证码需要"一次性消费"
/// （<c>ValidateAsync</c> 命中即删），单层存储避免 L1/L2 删除时机不一致导致的重放窗口。
/// 默认 <see cref="Microsoft.Extensions.Caching.Distributed.Memory.MemoryDistributedCache"/> 本身就是
/// 基于 <c>IMemoryCache</c> 的内存实现；生产 Redis 部署时自动是多副本共享。
/// </para>
///
/// <para><b>一次性消费的原子性</b>（review #7）：<see cref="IDistributedCache"/> 无 <c>GETDEL</c>
/// 原子原语，故 <see cref="TryGetAndRemoveAsync"/> 用 per-captchaId 锁 + "已消费哨兵"双重保证：
/// 首次校验读出真答案 → 立即覆盖写入空哨兵；并发第二次读出哨兵（空 answer）→ 视为已消费返回 null。
/// 生产 Redis 实现可用 <c>GETDEL</c> 进一步简化。</para>
/// </summary>
public sealed class CaptchaStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDistributedCache _cache;
    private readonly IOptions<CaptchaOptions> _options;

    // per-captchaId 锁：让 read-check-overwrite 序列化，避免并发两次校验都读到真答案。
    // 锁对象与 cache key 同生命周期 —— 用 ConcurrentDictionary 持有，验证后从字典移除让其 GC。
    private readonly ConcurrentDictionary<string, object> _consumeLocks = new();

    private const string KeyPrefix = "captcha:";
    private const string ConsumedSentinel = "";

    public CaptchaStore(
        IDistributedCache cache,
        IOptions<CaptchaOptions> options)
    {
        _cache = cache;
        _options = options;
    }

    public async Task SetAsync(string captchaId, string answer, CancellationToken ct)
    {
        var key = Key(captchaId);
        var entry = new CaptchaEntry(answer);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, Json);

        await _cache.SetAsync(key, bytes, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = _options.Value.Ttl,
        }, ct);
    }

    public async Task<string?> TryGetAndRemoveAsync(string captchaId, CancellationToken ct)
    {
        var key = Key(captchaId);
        var gate = _consumeLocks.GetOrAdd(key, _ => new object());

        string? answer;
        lock (gate)
        {
            // 锁内全 sync 读 + 覆盖，让并发两次校验第二次读到的是"已消费"哨兵。
            // IDistributedCache.Get/Set 有同步重载，锁内不 await yield，原子性由 lock + sync I/O 双重保证
            // （与 MultiLevelCache.TrySetAsync 同模式）。
            var bytes = _cache.Get(key);
            answer = bytes is { Length: > 0 }
                ? JsonSerializer.Deserialize<CaptchaEntry>(bytes, Json)?.Answer
                : null;

            // 覆盖为"已消费"哨兵（空 answer）—— 即便原 key 已被删，重复写空也无副作用。
            // 保留原 TTL 让哨兵自然过期，避免永久残留。
            if (answer is not null && answer != ConsumedSentinel)
            {
                _cache.Set(key,
                    JsonSerializer.SerializeToUtf8Bytes(new CaptchaEntry(ConsumedSentinel), Json),
                    new DistributedCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = _options.Value.Ttl,
                    });
            }
        }

        // 清理 per-key 锁对象，让其 GC（防 _consumeLocks 字典无界增长）
        _consumeLocks.TryRemove(key, out _);

        // 哨兵（已消费）或原本不存在 → 返回 null（视为校验失败）
        return string.IsNullOrEmpty(answer) ? null : answer;
    }

    private static string Key(string captchaId) => $"{KeyPrefix}{captchaId}";
}
