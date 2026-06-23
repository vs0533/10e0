using TenE0.Core.Abstractions;
using TenE0.Core.Errors;
using EntitySvc = TenE0.Core.EntityService.EntityService;
using TenE0.Core.Permissions;

namespace TenE0.Core.Tests.EntityService;

[Trait("Category", "Unit")]
public sealed class EntityServiceDeleteTests
{
    private sealed class TestProduct : IBaseEntity
    {
        public string Id { get; set; } = "";
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TestProduct> Products => Set<TestProduct>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestProduct>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Code).IsRequired(false);
            });
        }
    }

    private static TestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestDbContext(options);
    }

    // ══════════════════════════════════════════════════════════════
    //  1. Entity exists → deleted
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task EntityExists_ReturnsTrue()
    {
        await using var context = CreateContext();
        var entity = new TestProduct { Id = "1", Code = "ABC", Name = "Test" };
        context.Products.Add(entity);
        await context.SaveChangesAsync();

        var errs = new Errs();
        var permissionMock = new Mock<IPermissionEvaluator>();
        var sut = new EntitySvc(errs, permissionMock.Object);

        var result = await sut.DeleteAsync(context, entity);

        result.Should().BeTrue();
        var deleted = await context.Products.AsNoTracking().FirstOrDefaultAsync(e => e.Id == "1");
        deleted.Should().BeNull();
    }

    // ══════════════════════════════════════════════════════════════
    //  2. Entity not found
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task EntityNotFound_ReturnsFalse()
    {
        await using var context = CreateContext();
        var errs = new Errs();
        var permissionMock = new Mock<IPermissionEvaluator>();
        var sut = new EntitySvc(errs, permissionMock.Object);
        var entity = new TestProduct { Id = "999", Code = "GHI", Name = "Ghost" };

        var result = await sut.DeleteAsync(context, entity);

        result.Should().BeFalse();
        errs.Entries.Should().Contain(e => e.Code == "NOT_FOUND");
        errs.Entries.Should().Contain(e => e.Key == "Id");
    }

    // ══════════════════════════════════════════════════════════════
    //  3. Before "save" (errs.IsValid check) — errs invalid
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ErrsInvalid_ReturnsFalse()
    {
        await using var context = CreateContext();
        var entity = new TestProduct { Id = "1", Code = "ABC", Name = "Test" };
        context.Products.Add(entity);
        await context.SaveChangesAsync();

        var errs = new Errs();
        errs.Add("Pre-existing error", code: "PRE_ERR");
        var permissionMock = new Mock<IPermissionEvaluator>();
        var sut = new EntitySvc(errs, permissionMock.Object);

        var result = await sut.DeleteAsync(context, entity);

        // DeleteAsync removes the entity from the tracker, then checks errs.IsValid,
        // then fails at that check without calling SaveChanges
        result.Should().BeFalse();
        // Since SaveChanges was not called, the entity should still be in the DB
        var stillExists = await context.Products.AsNoTracking().AnyAsync(e => e.Id == "1");
        stillExists.Should().BeTrue();
    }

    // ══════════════════════════════════════════════════════════════
    //  4. Entity not in DB after successful delete
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task EntityNotInDbAfterDelete()
    {
        await using var context = CreateContext();
        var entity = new TestProduct { Id = "1", Code = "ABC", Name = "Test" };
        context.Products.Add(entity);
        await context.SaveChangesAsync();

        var errs = new Errs();
        var permissionMock = new Mock<IPermissionEvaluator>();
        var sut = new EntitySvc(errs, permissionMock.Object);

        await sut.DeleteAsync(context, entity);

        var count = await context.Products.AsNoTracking().CountAsync();
        count.Should().Be(0);
    }

    // ══════════════════════════════════════════════════════════════
    //  5. Empty table → entity doesn't exist
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DeleteEmptyTable_ReturnsFalse()
    {
        await using var context = CreateContext();
        var errs = new Errs();
        var permissionMock = new Mock<IPermissionEvaluator>();
        var sut = new EntitySvc(errs, permissionMock.Object);
        var entity = new TestProduct { Id = "1", Code = "ABC", Name = "Test" };

        var result = await sut.DeleteAsync(context, entity);

        result.Should().BeFalse();
        errs.Entries.Should().Contain(e => e.Code == "NOT_FOUND");
    }

    // ══════════════════════════════════════════════════════════════
    //  #111: stub entity（只 Id，必填字段缺失）不应触发必填校验异常
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// #111 回归守护：调用方传入 stub entity（只设了 Id，其他必填字段是默认值/null），
    /// DeleteAsync 应正常删除，而非因 EF Attach stub 时触发必填校验异常。
    /// 旧实现直接 Remove(stub) → EF Attach 缺失必填字段 → 抛校验异常掩盖"删除失败"语义。
    /// 新实现先加载 tracked 实体再删，绕开 stub 的必填校验问题。
    /// </summary>
    [Fact]
    public async Task DeleteStubEntity_WithRequiredFields_DoesNotThrowValidation()
    {
        // 用 SQLite 关系型 provider 才能验证必填约束（InMemory 不强制 IsRequired）
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        try
        {
            var options = new DbContextOptionsBuilder<StubTestDbContext>()
                .UseSqlite(connection)
                .Options;

            await using (var seedCtx = new StubTestDbContext(options))
            {
                await seedCtx.Database.EnsureCreatedAsync();
                seedCtx.StubProducts.Add(new StubProduct { Id = "1", Code = "ABC", Name = "Test" });
                await seedCtx.SaveChangesAsync();
            }

            await using var context = new StubTestDbContext(options);
            var errs = new Errs();
            var permissionMock = new Mock<IPermissionEvaluator>();
            var sut = new EntitySvc(errs, permissionMock.Object);

            // stub entity：只设 Id，必填字段 Code/Name 留默认（null）
            var stub = new StubProduct { Id = "1" };

            // 旧实现对 stub 直接 Remove 会抛异常；新实现加载 tracked 实体后删除，不抛
            var act = async () => await sut.DeleteAsync(context, stub);

            await act.Should().NotThrowAsync("DeleteAsync 应加载 tracked 实体再删，不应因 stub 必填字段缺失抛校验异常");
            (await context.StubProducts.AsNoTracking().AnyAsync(e => e.Id == "1"))
                .Should().BeFalse("实体应已被删除");
        }
        finally
        {
            await connection.CloseAsync();
            await connection.DisposeAsync();
        }
    }

    // #111: 带必填字段的测试实体 + DbContext（SQLite 才能强制 IsRequired）
    private sealed class StubProduct : IBaseEntity
    {
        public string Id { get; set; } = "";
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private sealed class StubTestDbContext(DbContextOptions<StubTestDbContext> options) : DbContext(options)
    {
        public DbSet<StubProduct> StubProducts => Set<StubProduct>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<StubProduct>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Code).IsRequired();
                entity.Property(e => e.Name).IsRequired();
            });
        }
    }
}
