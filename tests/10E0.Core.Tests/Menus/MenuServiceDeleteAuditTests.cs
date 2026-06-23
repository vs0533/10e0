using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using TenE0.Core.Abstractions;
using TenE0.Core.DataContext.Interceptors;
using TenE0.Core.Menus;
using TenE0.Core.Menus.Storage;
using StorageMenuType = TenE0.Core.Menus.Storage.MenuType;

namespace TenE0.Core.Tests.Menus;

/// <summary>
/// issue #95 [P1] 回归测试：MenuService.Delete 必须让 AuditInterceptor 接管审计字段，
/// 不再手动写 IsSoftDelete / DeleteTime / DeleteBy。FakeTimeProvider 验证 DeleteTime
/// 走 TimeProvider 而非 DateTimeOffset.UtcNow。
/// </summary>
[Trait("Category", "Unit")]
public sealed class MenuServiceDeleteAuditTests
{
    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TenE0Menu> Menus => Set<TenE0Menu>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TenE0Menu>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Ignore(e => e.Children);
                entity.Property(e => e.TreePath).IsRequired(false);
            });
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<TestDbContext> options)
        : IDbContextFactory<TestDbContext>
    {
        public TestDbContext CreateDbContext() => new(options);
    }

    private static TenE0Menu CreateMenu(string id, string name)
    {
        return new TenE0Menu
        {
            Id = id,
            Name = name,
            RoutePath = $"/{name.ToLower()}",
            TreePath = $"/{id}/",
            MenuType = StorageMenuType.Menu,
            IsActive = true,
            Layout = true,
        };
    }

    /// <summary>
    /// 显式注册 AuditInterceptor（生产代码的 TenE0SystemDbContext 默认注册；测试不继承框架
    /// DbContext 链，所以这里手动 AddInterceptor 让 SaveChangesAsync 触发拦截器）。
    /// #95 captive-dependency 修复后 AuditInterceptor 唯一 ctor 是 (IServiceProvider, IHttpContextAccessor, TimeProvider)。
    /// mock 出带 HttpContext.RequestServices 的 accessor，让拦截器在 SavingChanges 时能解析 ICurrentUserContext。
    /// </summary>
    private static DbContextOptions<TestDbContext> CreateOptionsWithAuditInterceptor(
        string dbName, TimeProvider timeProvider, ICurrentUserContext currentUser)
    {
        var mockSp = new Mock<IServiceProvider>();
        mockSp.Setup(sp => sp.GetService(typeof(ICurrentUserContext))).Returns(currentUser);
        var mockHttp = new Mock<HttpContext>();
        mockHttp.Setup(h => h.RequestServices).Returns(mockSp.Object);
        var mockAccessor = new Mock<IHttpContextAccessor>();
        mockAccessor.Setup(a => a.HttpContext).Returns(mockHttp.Object);
        var interceptor = new AuditInterceptor(mockSp.Object, mockAccessor.Object, timeProvider);
        return new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(dbName)
            .AddInterceptors(interceptor)
            .Options;
    }

    [Fact]
    public async Task DeleteAsync_RemovesViaChangeTracker_AuditInterceptorFillsDeleteFields()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var fixedTime = new DateTimeOffset(2026, 6, 22, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(fixedTime);

        var currentUser = new Mock<ICurrentUserContext>();
        currentUser.SetupGet(c => c.IsAuthenticated).Returns(true);
        currentUser.SetupGet(c => c.UserCode).Returns("alice");
        currentUser.SetupGet(c => c.RoleIds).Returns(new List<string>());

        var options = CreateOptionsWithAuditInterceptor(dbName, timeProvider, currentUser.Object);
        var service = new MenuService<TestDbContext>(new TestDbContextFactory(options), currentUser.Object);

        await using (var seed = new TestDbContext(options))
        {
            seed.Menus.Add(CreateMenu("menu-1", "Dashboard"));
            await seed.SaveChangesAsync();
        }

        // Act
        await service.DeleteAsync("menu-1");

        // Assert：审计字段由 AuditInterceptor 填，不依赖 MenuService 手动赋值
        await using var verify = new TestDbContext(options);
        var menu = await verify.Menus.AsNoTracking().FirstAsync(m => m.Id == "menu-1");
        menu.IsSoftDelete.Should().BeTrue();
        menu.DeleteTime.Should().Be(fixedTime, "DeleteTime 必须走 TimeProvider 而非 DateTimeOffset.UtcNow（issue #95 修复）");
        menu.DeleteBy.Should().Be("alice", "DeleteBy 必须从 ICurrentUserContext 取（不依赖 MenuService 手动赋值）");
    }

    [Fact]
    public async Task DeleteAsync_FakeTimeProviderAdvances_DeleteTimeReflectsFakeTime()
    {
        // Arrange：开始时间 12:00，删除前进到 14:30
        var start = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(start);

        var currentUser = new Mock<ICurrentUserContext>();
        currentUser.SetupGet(c => c.IsAuthenticated).Returns(true);
        currentUser.SetupGet(c => c.UserCode).Returns("bob");
        currentUser.SetupGet(c => c.RoleIds).Returns(new List<string>());

        var options = CreateOptionsWithAuditInterceptor(Guid.NewGuid().ToString(), timeProvider, currentUser.Object);
        var service = new MenuService<TestDbContext>(new TestDbContextFactory(options), currentUser.Object);

        await using (var seed = new TestDbContext(options))
        {
            seed.Menus.Add(CreateMenu("menu-2", "Settings"));
            await seed.SaveChangesAsync();
        }

        // 模拟 2.5 小时后
        timeProvider.Advance(TimeSpan.FromMinutes(150));

        // Act
        await service.DeleteAsync("menu-2");

        // Assert：DeleteTime 必须 = 推进后的 14:30
        await using var verify = new TestDbContext(options);
        var menu = await verify.Menus.AsNoTracking().FirstAsync(m => m.Id == "menu-2");
        var expected = start.AddMinutes(150);
        menu.IsSoftDelete.Should().BeTrue();
        menu.DeleteTime.Should().Be(expected);
        menu.DeleteBy.Should().Be("bob");
    }
}
