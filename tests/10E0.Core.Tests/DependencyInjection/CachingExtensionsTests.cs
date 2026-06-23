using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.Caching;
using TenE0.Core.DependencyInjection;

namespace TenE0.Core.Tests.DependencyInjection;

[Trait("Category", "Unit")]
[Trait("Category", "FragileReflection")]
public sealed class CachingExtensionsTests
{
    private static ServiceCollection NewServices() => new();

    // ── #101: 默认 MemoryCache 加 SizeLimit 兜底 OOM ────────────────────────────

    [Fact]
    public void AddTenE0Caching_NoArgs_RegistersMemoryCache_WithDefaultSizeLimit()
    {
        // Arrange
        var services = NewServices();

        // Act
        services.AddTenE0Caching();

        // Assert — 解析 IMemoryCache 后查看其内部 Options.SizeLimit 必须非 null 且 > 0
        using var sp = services.BuildServiceProvider();
        var cache = sp.GetRequiredService<IMemoryCache>();

        cache.Should().NotBeNull();
        // MemoryCache 不暴露 Options 公共属性，通过反射取 Options.SizeLimit 来断言。
        // 这是验证 default 真实下达到底层 MemoryCache 的唯一可靠方法。
        var sizeLimit = GetMemoryCacheSizeLimit(cache);
        sizeLimit.Should().NotBeNull("SizeLimit 必须显式配置；null 会让恶意构造的 roleCode 调用灌满进程内存");
        sizeLimit.Should().BeGreaterThan(0);
        sizeLimit.Should().Be(CachingOptions.DefaultSizeLimit);
    }

    [Fact]
    public void AddTenE0Caching_WithConfigure_AppliesCustomSizeLimit()
    {
        // Arrange
        var services = NewServices();

        // Act — Func<CachingOptions, CachingOptions> 允许 lambda 用 `with` 表达式
        services.AddTenE0Caching(opts => opts with { SizeLimit = 4096L });

        // Assert
        using var sp = services.BuildServiceProvider();
        var cache = sp.GetRequiredService<IMemoryCache>();
        GetMemoryCacheSizeLimit(cache).Should().Be(4096L);
    }

    [Fact]
    public void AddTenE0Caching_WithConfigure_AppliesCustomCompactionPercentage()
    {
        // Arrange
        var services = NewServices();

        // Act
        services.AddTenE0Caching(opts => opts with { CompactionPercentage = 0.25 });

        // Assert
        using var sp = services.BuildServiceProvider();
        var cache = sp.GetRequiredService<IMemoryCache>();
        GetMemoryCacheCompactionPercentage(cache).Should().Be(0.25);
    }

    [Fact]
    public void AddTenE0Caching_NotCalled_DoesNotRegisterIMemoryCache()
    {
        // Arrange — 仅注册其他服务，不调 AddTenE0Caching
        var services = NewServices();
        services.AddLogging();

        // Act + Assert
        using var sp = services.BuildServiceProvider();
        var act = () => sp.GetRequiredService<IMemoryCache>();
        act.Should().Throw<InvalidOperationException>(
            "AddTenE0Caching 必须保留 TryAdd 语义 —— 用户不调用就不应注册 IMemoryCache");
    }

    [Fact]
    public void AddTenE0Caching_CalledTwice_DoesNotDuplicateRegistration()
    {
        // Arrange
        var services = NewServices();

        // Act
        services.AddTenE0Caching();
        services.AddTenE0Caching();

        // Assert — TryAddSingleton 语义保证幂等
        services.Count(s => s.ServiceType == typeof(IMemoryCache))
            .Should().Be(1, "AddTenE0Caching 必须幂等，重复调用不能产生多个 IMemoryCache 注册");
    }

    [Fact]
    public void CachingOptions_Default_HasSizeLimitGreaterThanZero()
    {
        // 边界：默认值必须非零，否则失去 OOM 防护意义
        CachingOptions.Default.SizeLimit.Should().BeGreaterThan(0L);
        CachingOptions.Default.CompactionPercentage.Should().BeInRange(0.0, 1.0);
    }

    [Fact]
    public void AddTenE0Caching_WithConfiguration_AppliesValuesFromConfigSection()
    {
        // Arrange — IConfiguration 重载必须真实读 "Caching" 节并下沉到 MemoryCache
        var services = NewServices();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Caching:SizeLimit"] = "8192",
                ["Caching:CompactionPercentage"] = "0.10",
            })
            .Build();

        // Act
        services.AddTenE0Caching(config);

        // Assert
        using var sp = services.BuildServiceProvider();
        var cache = sp.GetRequiredService<IMemoryCache>();
        GetMemoryCacheSizeLimit(cache).Should().Be(8192L);
        GetMemoryCacheCompactionPercentage(cache).Should().Be(0.10);

        // IOptions<CachingOptions> 也必须可解析（业务项目可通过 IOptions<> 读取）
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CachingOptions>>()
            .Value.SizeLimit.Should().Be(8192L);
    }

    // MemoryCache 没有公开 Options 属性的版本（私有 _options 字段）。
    // 通过反射读 _options 字段验证配置真实下沉。
    // MED-2: .NET 升级时 _options 字段可能改名 —— 加显式 null 断言 + Trait 标记
    // 让 CI 失败时定位明确（"反射读不到 MemoryCache._options 字段，请更新测试"）。
    private static long? GetMemoryCacheSizeLimit(IMemoryCache cache)
    {
        var options = GetMemoryCacheOptions(cache);
        return options?.SizeLimit;
    }

    private static double? GetMemoryCacheCompactionPercentage(IMemoryCache cache)
    {
        var options = GetMemoryCacheOptions(cache);
        return options?.CompactionPercentage;
    }

    private static MemoryCacheOptions? GetMemoryCacheOptions(IMemoryCache cache)
    {
        var field = cache.GetType().GetField("_options",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field.Should().NotBeNull(
            "MemoryCache._options field renamed or removed in this .NET version — update reflection in CachingExtensionsTests");
        return field?.GetValue(cache) as MemoryCacheOptions;
    }
}
