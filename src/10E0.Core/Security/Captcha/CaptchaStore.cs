using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace TenE0.Core.Security.Captcha;

/// <summary>
/// 验证码缓存条目（落 <see cref="IDistributedCache"/>，序列化为 JSON 字节）。
/// </summary>
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
/// </summary>
public sealed class CaptchaStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDistributedCache _cache;
    private readonly IOptions<CaptchaOptions> _options;

    private const string KeyPrefix = "captcha:";

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

        // 一次性消费：先读答案再删除（无论命中与否，防重放）
        string? answer = null;
        var bytes = await _cache.GetAsync(key, ct);
        if (bytes is { Length: > 0 })
        {
            var entry = JsonSerializer.Deserialize<CaptchaEntry>(bytes, Json);
            answer = entry?.Answer;
        }

        await _cache.RemoveAsync(key, ct);

        return answer;
    }

    private static string Key(string captchaId) => $"{KeyPrefix}{captchaId}";
}
