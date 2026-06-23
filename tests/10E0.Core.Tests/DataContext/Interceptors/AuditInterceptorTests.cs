using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using TenE0.Core.Abstractions;
using TenE0.Core.DataContext.Interceptors;

namespace TenE0.Core.Tests.DataContext.Interceptors;

[Trait("Category", "Unit")]
public sealed class AuditInterceptorTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly Mock<ICurrentUserContext> _currentUser = CreateUser("test-user");

    private sealed class TestEntity : ITimerEntity, ISoftDeleteEntity
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public DateTimeOffset? CreateTime { get; set; }
        public string? CreateBy { get; set; }
        public DateTimeOffset? UpdateTime { get; set; }
        public string? UpdateBy { get; set; }
        public bool IsSoftDelete { get; set; }
        public DateTimeOffset? DeleteTime { get; set; }
        public string? DeleteBy { get; set; }
    }

    private sealed class NonTimedEntity : IBaseEntity
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string? Name { get; set; }
    }

    private sealed class TestDbContext : DbContext
    {
        private readonly AuditInterceptor _interceptor;
        public DbSet<TestEntity> TestEntities => Set<TestEntity>();
        public DbSet<NonTimedEntity> NonTimedEntities => Set<NonTimedEntity>();

        public TestDbContext(DbContextOptions<TestDbContext> options, AuditInterceptor interceptor) : base(options)
            => _interceptor = interceptor;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            optionsBuilder.AddInterceptors(_interceptor);
        }
    }

    private static Mock<ICurrentUserContext> CreateUser(string userCode)
    {
        var mock = new Mock<ICurrentUserContext>();
        mock.SetupGet(c => c.IsAuthenticated).Returns(true);
        mock.SetupGet(c => c.UserCode).Returns(userCode);
        mock.SetupGet(c => c.RoleIds).Returns([]);
        return mock;
    }

    /// <summary>
    /// #95 captive-dependency 修复后，AuditInterceptor 唯一构造器是 (IServiceProvider, IHttpContextAccessor, TimeProvider)。
    /// 测试 mock IHttpContextAccessor.HttpContext.RequestServices.GetService&lt;ICurrentUserContext&gt;()
    /// 返回当前 mock 用户，模拟"请求 scope 内能拿到 ICurrentUserContext"的真实路径。
    /// </summary>
    private TestDbContext CreateDbContext()
    {
        var mockSp = new Mock<IServiceProvider>();
        mockSp.Setup(sp => sp.GetService(typeof(ICurrentUserContext))).Returns(_currentUser.Object);

        var mockHttp = new Mock<HttpContext>();
        mockHttp.Setup(h => h.RequestServices).Returns(mockSp.Object);

        var mockAccessor = new Mock<IHttpContextAccessor>();
        mockAccessor.Setup(a => a.HttpContext).Returns(mockHttp.Object);

        var interceptor = new AuditInterceptor(mockSp.Object, mockAccessor.Object, _timeProvider);
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestDbContext(options, interceptor);
    }

    [Fact]
    public async Task AddedEntity_FillsCreateTimeAndCreateBy()
    {
        using var db = CreateDbContext();
        var entity = new TestEntity();
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero));

        db.TestEntities.Add(entity);
        await db.SaveChangesAsync();

        entity.CreateTime.Should().Be(new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero));
        entity.CreateBy.Should().Be("test-user");
        entity.UpdateTime.Should().BeNull();
        entity.UpdateBy.Should().BeNull();
    }

    [Fact]
    public async Task ModifiedEntity_FillsUpdateTimeAndUpdateBy_PreservesCreateTime()
    {
        using var db = CreateDbContext();
        var entity = new TestEntity();

        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero));
        db.TestEntities.Add(entity);
        await db.SaveChangesAsync();
        var originalCreateTime = entity.CreateTime;

        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero));
        db.Entry(entity).State = EntityState.Modified;
        await db.SaveChangesAsync();

        entity.CreateTime.Should().Be(originalCreateTime);
        entity.UpdateTime.Should().Be(new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero));
        entity.UpdateBy.Should().Be("test-user");
    }

    [Fact]
    public async Task DeletedSoftDeleteEntity_ConvertsToModified_AndSetsDeleteFields()
    {
        using var db = CreateDbContext();
        var entity = new TestEntity();

        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero));
        db.TestEntities.Add(entity);
        await db.SaveChangesAsync();

        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 7, 1, 11, 0, 0, TimeSpan.Zero));
        db.TestEntities.Remove(entity);
        await db.SaveChangesAsync();

        entity.IsSoftDelete.Should().BeTrue();
        entity.DeleteTime.Should().Be(new DateTimeOffset(2026, 7, 1, 11, 0, 0, TimeSpan.Zero));
        entity.DeleteBy.Should().Be("test-user");

        var dbEntity = await db.TestEntities.IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.Id == entity.Id);
        dbEntity.Should().NotBeNull();
        dbEntity!.IsSoftDelete.Should().BeTrue();
    }

    [Fact]
    public async Task NonTimedEntity_PassesThrough_WithoutModification()
    {
        using var db = CreateDbContext();
        var entity = new NonTimedEntity { Name = "test" };

        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero));
        db.NonTimedEntities.Add(entity);
        await db.SaveChangesAsync();

        entity.Name.Should().Be("test");
        var dbEntity = await db.NonTimedEntities.FindAsync(entity.Id);
        dbEntity.Should().NotBeNull();
    }
}
