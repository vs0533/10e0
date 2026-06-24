using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TenE0.Core.Abstractions;
using TenE0.Core.Caching;
using TenE0.Core.Configuration.Storage;
using TenE0.Core.Events;

namespace TenE0.Core.Configuration;

/// <summary>
/// 系统参数存储实现 — Key-Value 类型化读取 + 运行时修改，带多级缓存与变更通知。
///
/// <para>
/// 缓存约束：<see cref="IMultiLevelCache"/> 要求 <c>T : class</c>，故值经
/// <see cref="ParameterValueBox"/> 装箱后缓存。失效策略：逐 key 精准
/// <see cref="IMultiLevelCache.RemoveAsync"/>（Issue #153 决策点 3）。
/// </para>
/// </summary>
public sealed class SystemParameterStore<TContext>(
    IDbContextFactory<TContext> contextFactory,
    IMultiLevelCache cache,
    ICacheKeyNamespace keyNamespace,
    SystemParameterRegistry registry,
    IOptions<ConfigurationOptions> options,
    IDomainEventDispatcher? eventDispatcher) : ISystemParameterStore
    where TContext : DbContext
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TimeSpan _cacheL2 = options.Value.ParamCacheL2;

    private CacheOptions ValueCacheOptions() => new()
    {
        L1Duration = TimeSpan.FromSeconds(5),
        L2Duration = _cacheL2,
    };

    private string CacheKey(string key) => keyNamespace.SystemParameterKey(key);

    public async Task<T?> GetAsync<T>(string key, T? defaultValue = default, CancellationToken cancellationToken = default)
    {
        var box = await cache.GetOrSetAsync(
            CacheKey(key),
            ct => LoadBoxFromDbAsync(key, ct),
            ValueCacheOptions(),
            cancellationToken);

        if (box is null)
            return defaultValue;

        try
        {
            return ConvertValue<T>(box.Value, box.ValueType);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or OverflowException)
        {
            // 类型转换失败：参数表数据损坏，降级为默认值（不抛 —— 读取热路径不应因脏数据中断业务）
            return defaultValue;
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        // 仅预定义 key 可改
        var definition = registry.GetDefinition(key)
            ?? throw new InvalidOperationException($"系统参数未定义，拒绝修改：{key}");

        await using var dc = await contextFactory.CreateDbContextAsync(cancellationToken);

        var param = await dc.Set<TenE0SystemParameter>()
            .FirstOrDefaultAsync(p => p.Key == key, cancellationToken)
            ?? throw new InvalidOperationException($"系统参数不存在：{key}");

        if (param.IsReadOnly)
            throw new InvalidOperationException($"系统参数只读，拒绝修改：{key}");

        // 仅预定义类型校验值格式（DB 中存在但未注册的历史 key 不强校验）
        if (!IsValueValid(value, definition.ValueType))
            throw new InvalidOperationException($"系统参数值格式不合法（期望 {definition.ValueType}）：{key}");

        var oldValue = param.Value;
        param.Value = value;
        await dc.SaveChangesAsync(cancellationToken);

        // 写后：失效缓存 + 派发变更事件
        await cache.RemoveAsync(CacheKey(key), cancellationToken);
        if (eventDispatcher is not null)
            await eventDispatcher.DispatchAsync(
                new SystemParameterChangedEvent(key, oldValue, value), cancellationToken);
    }

    public async Task<IReadOnlyList<SystemParameterDto>> GetByGroupAsync(string group, CancellationToken cancellationToken = default)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await dc.Set<TenE0SystemParameter>()
            .AsNoTracking()
            .Where(p => p.Group == group)
            .OrderBy(p => p.Key)
            .Select(p => new SystemParameterDto
            {
                Id = p.Id,
                Key = p.Key,
                Value = p.Value,
                ValueType = p.ValueType,
                Description = p.Description,
                Group = p.Group,
                IsReadOnly = p.IsReadOnly,
                IsHidden = p.IsHidden,
            })
            .ToListAsync(cancellationToken);
    }

    // ============================================================
    // 类型化转换
    // ============================================================

    private async ValueTask<ParameterValueBox?> LoadBoxFromDbAsync(string key, CancellationToken ct)
    {
        await using var dc = await contextFactory.CreateDbContextAsync(ct);
        var p = await dc.Set<TenE0SystemParameter>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == key, ct);
        if (p is null) return null;
        return new ParameterValueBox(p.Value, p.ValueType);
    }

    /// <summary>
    /// 按声明的 <paramref name="type"/> 把字符串原始值转换为 <typeparamref name="T"/>。
    /// 标量走 Parse；Json 走 System.Text.Json 反序列化；String（含 fallback）原样返回。
    /// </summary>
    private static T? ConvertValue<T>(string raw, ParameterValueType type)
    {
        var underlying = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return type switch
        {
            ParameterValueType.Int => (T)(object)int.Parse(raw),
            ParameterValueType.Bool => (T)(object)bool.Parse(raw),
            ParameterValueType.Decimal => (T)(object)decimal.Parse(raw),
            // Json：字符串目标直接原样返回，否则反序列化到目标类型
            ParameterValueType.Json when underlying == typeof(string) => (T)(object)raw,
            ParameterValueType.Json => (T)JsonSerializer.Deserialize(raw, underlying, JsonOptions)!,
            _ => (T)(object)raw, // String
        };
    }

    /// <summary>按声明类型校验值格式是否可转换（仅用于 Set 时的预校验）。</summary>
    private static bool IsValueValid(string value, ParameterValueType type) => type switch
    {
        ParameterValueType.Int => int.TryParse(value, out _),
        ParameterValueType.Bool => bool.TryParse(value, out _),
        ParameterValueType.Decimal => decimal.TryParse(value, out _),
        ParameterValueType.Json => IsValidJson(value),
        _ => true, // String
    };

    private static bool IsValidJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            using var doc = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>缓存值装箱（满足 <c>T : class</c> 约束）。</summary>
    private sealed class ParameterValueBox
    {
        public string Value { get; }
        public ParameterValueType ValueType { get; }

        public ParameterValueBox(string value, ParameterValueType valueType)
        {
            Value = value;
            ValueType = valueType;
        }
    }
}
