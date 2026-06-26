using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using TenE0.Core.Abstractions;
using TenE0.Core.Caching;
using TenE0.Core.Configuration;
using TenE0.Core.Configuration.Storage;
using TenE0.Core.Events;

namespace TenE0.Core.Tests.Configuration;

/// <summary>
/// 数据字典服务单测 — CRUD + 缓存命中/失效 + 树形组装。
/// 镜像 <c>OrgTreeServiceTests</c> 范式：嵌套 TestDbContext/Factory（InMemory）。
/// 缓存用 Mock&lt;IMultiLevelCache&gt;：GetOrSetAsync 回调 factory 走真实 DB，
/// 从而同时验证回源路径与写后失效（RemoveAsync 被调用）。
/// </summary>
[Trait("Category", "Unit")]
public sealed class DataDictionaryServiceTests
{
    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TenE0DictType> DictTypes => Set<TenE0DictType>();
        public DbSet<TenE0DictItem> DictItems => Set<TenE0DictItem>();

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

    // 简易进程内 IMultiLevelCache 替身：命中即返回、miss 调 factory；RemoveAsync 删 key。
    // 避免 Moq 匹配开放泛型 GetOrSetAsync<T> 的麻烦，且更贴近真实缓存命中/失效行为。
    private sealed class FakeMultiLevelCache : IMultiLevelCache
    {
        private readonly Dictionary<string, object?> _store = new();
        public int FactoryCalls { get; private set; }

        public Task<T?> GetOrSetAsync<T>(
            string key, Func<CancellationToken, ValueTask<T?>> factory,
            CacheOptions options, CancellationToken cancellationToken = default) where T : class
        {
            if (_store.TryGetValue(key, out var v))
                return Task.FromResult((T?)v);
            return RunAsync(key, factory, cancellationToken);

            async Task<T?> RunAsync(string k, Func<CancellationToken, ValueTask<T?>> f, CancellationToken ct)
            {
                FactoryCalls++;
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

        public bool Contains(string key) => _store.ContainsKey(key);
        public bool WasRemoved { get; set; }
    }

    private static DataDictionaryService<TestDbContext> CreateService(
        string dbName,
        FakeMultiLevelCache? cache = null,
        Mock<IDomainEventDispatcher>? dispatcherMock = null)
    {
        cache ??= new FakeMultiLevelCache();
        var ns = new DefaultCacheKeyNamespace();
        var options = Options.Create(new ConfigurationOptions());
        return new DataDictionaryService<TestDbContext>(
            CreateFactory(dbName), cache, ns, options, dispatcherMock?.Object);
    }

    // ================================================================
    // CRUD
    // ================================================================

    [Fact]
    public async Task AddTypeAsync_PersistsTypeAndReturnsDto()
    {
        var svc = CreateService(Guid.NewGuid().ToString("N"));

        var dto = await svc.AddTypeAsync(new DictTypeCreateRequest("gender", "性别", null), default);

        dto.Code.Should().Be("gender");
        var types = await svc.GetTypesAsync();
        types.Should().ContainSingle().Which.Code.Should().Be("gender");
    }

    [Fact]
    public async Task AddTypeAsync_DuplicateCode_Throws()
    {
        var svc = CreateService(Guid.NewGuid().ToString("N"));
        await svc.AddTypeAsync(new DictTypeCreateRequest("gender", "性别", null), default);

        var act = () => svc.AddTypeAsync(new DictTypeCreateRequest("gender", "性别2", null), default);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AddItemAsync_PersistsItemUnderType()
    {
        var svc = CreateService(Guid.NewGuid().ToString("N"));
        await svc.AddTypeAsync(new DictTypeCreateRequest("gender", "性别", null), default);

        var item = await svc.AddItemAsync("gender", new DictItemCreateRequest("男", "M", null), default);

        item.Value.Should().Be("M");
        var items = await svc.GetItemsAsync("gender");
        items.Should().ContainSingle().Which.Value.Should().Be("M");
    }

    [Fact]
    public async Task AddItemAsync_DuplicateValue_Throws()
    {
        var svc = CreateService(Guid.NewGuid().ToString("N"));
        await svc.AddTypeAsync(new DictTypeCreateRequest("gender", "性别", null), default);
        await svc.AddItemAsync("gender", new DictItemCreateRequest("男", "M", null), default);

        var act = () => svc.AddItemAsync("gender", new DictItemCreateRequest("男2", "M", null), default);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UpdateItemAsync_ModifiesFields()
    {
        var svc = CreateService(Guid.NewGuid().ToString("N"));
        await svc.AddTypeAsync(new DictTypeCreateRequest("gender", "性别", null), default);
        await svc.AddItemAsync("gender", new DictItemCreateRequest("男", "M", null), default);

        await svc.UpdateItemAsync("gender", "M", new DictItemUpdateRequest(
            Label: "男性", Value: null, ExtraJson: null, IsEnabled: false, SortOrder: 9, ParentItemValue: null), default);

        var item = await svc.GetItemByValueAsync("gender", "M", onlyEnabled: false);
        item!.Label.Should().Be("男性");
        item.IsEnabled.Should().BeFalse();
        item.SortOrder.Should().Be(9);
    }

    [Fact]
    public async Task DeleteItemAsync_RemovesItem()
    {
        var svc = CreateService(Guid.NewGuid().ToString("N"));
        await svc.AddTypeAsync(new DictTypeCreateRequest("gender", "性别", null), default);
        await svc.AddItemAsync("gender", new DictItemCreateRequest("男", "M", null), default);

        await svc.DeleteItemAsync("gender", "M", default);

        var items = await svc.GetItemsAsync("gender", onlyEnabled: false);
        items.Should().BeEmpty();
    }

    // ================================================================
    // 缓存：写后失效
    // ================================================================

    [Fact]
    public async Task WriteOperation_InvalidatesCacheForType()
    {
        var cache = new FakeMultiLevelCache();
        var svc = CreateService(Guid.NewGuid().ToString("N"), cache);
        await svc.AddTypeAsync(new DictTypeCreateRequest("gender", "性别", null), default);
        await svc.AddItemAsync("gender", new DictItemCreateRequest("男", "M", null), default);

        // 读一次填充缓存，再写 —— 写后缓存应被清空
        await svc.GetItemsAsync("gender");
        cache.Contains("dict-items:gender").Should().BeTrue();

        await svc.AddItemAsync("gender", new DictItemCreateRequest("女", "F", null), default);

        cache.Contains("dict-items:gender").Should().BeFalse(
            "每次写都应精准失效该 typeCode 的缓存 key");
    }

    [Fact]
    public async Task GetItemsAsync_CachesAndSecondReadHitsWithoutReload()
    {
        var cache = new FakeMultiLevelCache();
        var svc = CreateService(Guid.NewGuid().ToString("N"), cache);
        await svc.AddTypeAsync(new DictTypeCreateRequest("gender", "性别", null), default);
        await svc.AddItemAsync("gender", new DictItemCreateRequest("男", "M", null), default);

        await svc.GetItemsAsync("gender");
        var callsAfterFirst = cache.FactoryCalls;
        await svc.GetItemsAsync("gender");

        // 第二次读应命中缓存，不再回源
        cache.FactoryCalls.Should().Be(callsAfterFirst,
            "第二次 GetItemsAsync 应命中 L1 缓存，不调用回源 factory");
    }

    // ================================================================
    // 树形组装
    // ================================================================

    [Fact]
    public async Task GetItemsAsync_AsTree_BuildsHierarchyByParentItemValue()
    {
        var svc = CreateService(Guid.NewGuid().ToString("N"));
        await svc.AddTypeAsync(new DictTypeCreateRequest("area", "地区", null), default);
        await svc.AddItemAsync("area", new DictItemCreateRequest("中国", "CN", null), default);
        await svc.AddItemAsync("area", new DictItemCreateRequest("广东", "GD", null, ParentItemValue: "CN"), default);
        await svc.AddItemAsync("area", new DictItemCreateRequest("广州", "GZ", null, ParentItemValue: "GD"), default);

        var tree = await svc.GetItemsAsync("area", onlyEnabled: false, asTree: true);

        tree.Should().ContainSingle(n => n.Value == "CN");
        var cn = tree.Single(n => n.Value == "CN");
        cn.Children.Should().ContainSingle(n => n.Value == "GD");
        var gd = cn.Children.Single(n => n.Value == "GD");
        gd.Children.Should().ContainSingle(n => n.Value == "GZ");
    }

    // ================================================================
    // 事件：写后派发 DictChangedEvent
    // ================================================================

    [Fact]
    public async Task WriteOperation_DispatchesDictChangedEvent()
    {
        var dispatcherMock = new Mock<IDomainEventDispatcher>();
        var svc = CreateService(Guid.NewGuid().ToString("N"), dispatcherMock: dispatcherMock);
        await svc.AddTypeAsync(new DictTypeCreateRequest("gender", "性别", null), default);

        await svc.AddItemAsync("gender", new DictItemCreateRequest("男", "M", null), default);

        dispatcherMock.Verify(
            d => d.DispatchAsync(
                It.Is<DictChangedEvent>(e => e.DictTypeCode == "gender"),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce());
    }
}
