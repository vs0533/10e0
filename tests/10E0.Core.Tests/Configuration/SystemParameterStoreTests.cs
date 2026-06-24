using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using TenE0.Core.Abstractions;
using TenE0.Core.Caching;
using TenE0.Core.Configuration;
using TenE0.Core.Configuration.Storage;
using TenE0.Core.Events;

namespace TenE0.Core.Tests.Configuration;

/// <summary>
/// 系统参数存储单测 — 类型化读取 / 预定义 key 校验 / 只读拒绝 / 写后失效+事件。
/// 镜像 <c>OrgTreeServiceTests</c> 范式（嵌套 TestDbContext/Factory + InMemory）。
/// </summary>
[Trait("Category", "Unit")]
public sealed class SystemParameterStoreTests
{
    // ---- 测试用参数定义 ----
    private sealed record IntDef : ISystemParameterDefinition
    {
        public string Key => "password.min_length";
        public string DefaultValue => "8";
        public ParameterValueType ValueType => ParameterValueType.Int;
        public string Group => "Security";
        public string? Description => null;
        public bool IsReadOnly => false;
    }

    private sealed record BoolDef : ISystemParameterDefinition
    {
        public string Key => "business.flag";
        public string DefaultValue => "false";
        public ParameterValueType ValueType => ParameterValueType.Bool;
        public string Group => "Business";
        public string? Description => null;
        public bool IsReadOnly => false;
    }

    private sealed record DecimalDef : ISystemParameterDefinition
    {
        public string Key => "rate.value";
        public string DefaultValue => "1.5";
        public ParameterValueType ValueType => ParameterValueType.Decimal;
        public string Group => "Biz";
        public string? Description => null;
        public bool IsReadOnly => false;
    }

    private sealed record JsonDef : ISystemParameterDefinition
    {
        public string Key => "rate.limits";
        public string DefaultValue => """{"perMinute":100}""";
        public ParameterValueType ValueType => ParameterValueType.Json;
        public string Group => "Biz";
        public string? Description => null;
        public bool IsReadOnly => false;
    }

    private sealed record ReadOnlyDef : ISystemParameterDefinition
    {
        public string Key => "system.locked";
        public string DefaultValue => "fixed";
        public ParameterValueType ValueType => ParameterValueType.String;
        public string Group => "System";
        public string? Description => null;
        public bool IsReadOnly => true;
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TenE0SystemParameter> SystemParameters => Set<TenE0SystemParameter>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            optionsBuilder.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ConfigureTenE0ConfigurationTables();
    }

    private sealed class TestDbContextFactory(DbContextOptions<TestDbContext> options) : IDbContextFactory<TestDbContext>
    {
        public TestDbContext CreateDbContext() => new(options);
    }

    private static IDbContextFactory<TestDbContext> CreateFactory(string dbName)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TestDbContextFactory(options);
    }

    // 简易进程内 IMultiLevelCache 替身：L1+L2 合并成一个字典，命中即返回、miss 调 factory；
    // RemoveAsync 删 key。避免 Moq 匹配开放泛型 GetOrSetAsync<T> 的麻烦，且更贴近真实缓存行为。
    private sealed class FakeMultiLevelCache : IMultiLevelCache
    {
        private readonly Dictionary<string, object?> _store = new();

        public Task<T?> GetOrSetAsync<T>(
            string key, Func<CancellationToken, ValueTask<T?>> factory,
            CacheOptions options, CancellationToken cancellationToken = default) where T : class
        {
            if (_store.TryGetValue(key, out var v))
                return Task.FromResult((T?)v);
            return RunAsync(key, factory, cancellationToken);

            async Task<T?> RunAsync(string k, Func<CancellationToken, ValueTask<T?>> f, CancellationToken ct)
            {
                var val = await f(ct);
                _store[k] = val;
                return val;
            }
        }

        public Task<bool> TrySetAsync<T>(string key, T value, CacheOptions options, CancellationToken cancellationToken = default) where T : class
        {
            if (_store.ContainsKey(key)) return Task.FromResult(false);
            _store[key] = value;
            return Task.FromResult(true);
        }

        public Task SetAsync<T>(string key, T value, CacheOptions options, CancellationToken cancellationToken = default) where T : class
        {
            _store[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _store.Remove(key);
            return Task.CompletedTask;
        }

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
            => Task.FromResult(_store.TryGetValue(key, out var v) ? (T?)v : null);

        // 供测试断言：key 是否已被失效
        public bool Contains(string key) => _store.ContainsKey(key);
    }

    private static SystemParameterRegistry Registry(params ISystemParameterDefinition[] defs) => new(defs);

    private static SystemParameterStore<TestDbContext> CreateStore(
        string dbName,
        SystemParameterRegistry registry,
        FakeMultiLevelCache? cache = null,
        Mock<IDomainEventDispatcher>? dispatcherMock = null)
    {
        cache ??= new FakeMultiLevelCache();
        return new SystemParameterStore<TestDbContext>(
            CreateFactory(dbName), cache, new DefaultCacheKeyNamespace(),
            registry, Options.Create(new ConfigurationOptions()), dispatcherMock?.Object);
    }

    private static async Task SeedAsync(string dbName, params TenE0SystemParameter[] parameters)
    {
        await using var dc = await CreateFactory(dbName).CreateDbContextAsync();
        dc.SystemParameters.AddRange(parameters);
        await dc.SaveChangesAsync();
    }

    // ================================================================
    // 类型化读取
    // ================================================================

    [Fact]
    public async Task GetAsync_Int_ReturnsParsedInt()
    {
        var db = Guid.NewGuid().ToString("N");
        await SeedAsync(db, new TenE0SystemParameter { Key = "password.min_length", Value = "8", ValueType = ParameterValueType.Int });
        var store = CreateStore(db, Registry(new IntDef()));

        var value = await store.GetAsync("password.min_length", 0);

        value.Should().Be(8);
    }

    [Fact]
    public async Task GetAsync_Bool_ReturnsParsedBool()
    {
        var db = Guid.NewGuid().ToString("N");
        await SeedAsync(db, new TenE0SystemParameter { Key = "business.flag", Value = "true", ValueType = ParameterValueType.Bool });
        var store = CreateStore(db, Registry(new BoolDef()));

        var value = await store.GetAsync("business.flag", false);

        value.Should().BeTrue();
    }

    [Fact]
    public async Task GetAsync_Decimal_ReturnsParsedDecimal()
    {
        var db = Guid.NewGuid().ToString("N");
        await SeedAsync(db, new TenE0SystemParameter { Key = "rate.value", Value = "3.14", ValueType = ParameterValueType.Decimal });
        var store = CreateStore(db, Registry(new DecimalDef()));

        var value = await store.GetAsync("rate.value", 0m);

        value.Should().Be(3.14m);
    }

    [Fact]
    public async Task GetAsync_Json_DeserializesToTargetType()
    {
        var db = Guid.NewGuid().ToString("N");
        await SeedAsync(db, new TenE0SystemParameter { Key = "rate.limits", Value = """{"perMinute":250}""", ValueType = ParameterValueType.Json });
        var store = CreateStore(db, Registry(new JsonDef()));

        var value = await store.GetAsync<RateLimits>("rate.limits");

        value!.PerMinute.Should().Be(250);
    }

    [Fact]
    public async Task GetAsync_MissingKey_ReturnsDefault()
    {
        var store = CreateStore(Guid.NewGuid().ToString("N"), Registry(new IntDef()));

        var value = await store.GetAsync("does.not.exist", 42);

        value.Should().Be(42);
    }

    [Fact]
    public async Task GetAsync_CorruptValue_ReturnsDefaultInsteadOfThrowing()
    {
        var db = Guid.NewGuid().ToString("N");
        await SeedAsync(db, new TenE0SystemParameter { Key = "password.min_length", Value = "not-a-number", ValueType = ParameterValueType.Int });
        var store = CreateStore(db, Registry(new IntDef()));

        // 脏数据不应在读取热路径抛出，降级为默认
        var value = await store.GetAsync("password.min_length", 42);

        value.Should().Be(42);
    }

    private sealed class RateLimits { public int PerMinute { get; set; } }

    // ================================================================
    // Set 校验：预定义 key + 只读拒绝 + 值格式
    // ================================================================

    [Fact]
    public async Task SetAsync_PredefinedKey_UpdatesValue()
    {
        var db = Guid.NewGuid().ToString("N");
        await SeedAsync(db, new TenE0SystemParameter { Key = "password.min_length", Value = "8", ValueType = ParameterValueType.Int });
        var store = CreateStore(db, Registry(new IntDef()));

        await store.SetAsync("password.min_length", "12");

        await using var dc = await CreateFactory(db).CreateDbContextAsync();
        var p = await dc.SystemParameters.SingleAsync();
        p.Value.Should().Be("12");
    }

    [Fact]
    public async Task SetAsync_UndefinedKey_Throws()
    {
        var db = Guid.NewGuid().ToString("N");
        await SeedAsync(db, new TenE0SystemParameter { Key = "rogue.key", Value = "x", ValueType = ParameterValueType.String });
        var store = CreateStore(db, Registry()); // 空注册表 —— 所有 key 视为未定义

        var act = () => store.SetAsync("rogue.key", "y");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SetAsync_ReadOnlyKey_Throws()
    {
        var db = Guid.NewGuid().ToString("N");
        await SeedAsync(db, new TenE0SystemParameter { Key = "system.locked", Value = "fixed", ValueType = ParameterValueType.String, IsReadOnly = true });
        var store = CreateStore(db, Registry(new ReadOnlyDef()));

        var act = () => store.SetAsync("system.locked", "changed");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SetAsync_InvalidValueFormat_Throws()
    {
        var db = Guid.NewGuid().ToString("N");
        await SeedAsync(db, new TenE0SystemParameter { Key = "password.min_length", Value = "8", ValueType = ParameterValueType.Int });
        var store = CreateStore(db, Registry(new IntDef()));

        var act = () => store.SetAsync("password.min_length", "not-an-int");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ================================================================
    // 写后：失效缓存 + 派发事件
    // ================================================================

    [Fact]
    public async Task SetAsync_InvalidatesCacheAndDispatchesEvent()
    {
        var db = Guid.NewGuid().ToString("N");
        await SeedAsync(db, new TenE0SystemParameter { Key = "password.min_length", Value = "8", ValueType = ParameterValueType.Int });
        var cache = new FakeMultiLevelCache();
        var dispatcherMock = new Mock<IDomainEventDispatcher>();
        var store = CreateStore(db, Registry(new IntDef()), cache, dispatcherMock);

        // 先读一次填充缓存，再改值
        await store.GetAsync<int>("password.min_length");
        cache.Contains("sys-param:password.min_length").Should().BeTrue();

        await store.SetAsync("password.min_length", "12");

        cache.Contains("sys-param:password.min_length").Should().BeFalse("Set 后应精准失效该 key 缓存");
        dispatcherMock.Verify(
            d => d.DispatchAsync(
                It.Is<SystemParameterChangedEvent>(e => e.Key == "password.min_length" && e.OldValue == "8" && e.NewValue == "12"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ================================================================
    // GetByGroup
    // ================================================================

    [Fact]
    public async Task GetByGroupAsync_ReturnsParametersInGroup()
    {
        var db = Guid.NewGuid().ToString("N");
        await SeedAsync(db,
            new TenE0SystemParameter { Key = "password.min_length", Value = "8", ValueType = ParameterValueType.Int, Group = "Security" },
            new TenE0SystemParameter { Key = "other.key", Value = "x", ValueType = ParameterValueType.String, Group = "Other" });
        var store = CreateStore(db, Registry(new IntDef()));

        var result = await store.GetByGroupAsync("Security");

        result.Should().ContainSingle().Which.Key.Should().Be("password.min_length");
    }
}
