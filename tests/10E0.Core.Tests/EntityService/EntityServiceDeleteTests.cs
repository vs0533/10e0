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
}
