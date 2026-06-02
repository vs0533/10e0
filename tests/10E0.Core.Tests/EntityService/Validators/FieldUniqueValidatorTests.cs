using TenE0.Core.Abstractions;
using TenE0.Core.Errors;
using TenE0.Core.EntityService.Validators;

namespace TenE0.Core.Tests.EntityService.Validators;

[Trait("Category", "Unit")]
public sealed class FieldUniqueValidatorTests
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
                entity.Property(e => e.Id).ValueGeneratedOnAdd();
                entity.Property(e => e.Code).IsRequired(false);
                entity.Property(e => e.Name).IsRequired(false);
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

    [Fact]
    public async Task SingleField_NoDuplicate_ReturnsValid()
    {
        await using var context = CreateContext();
        var entity = new TestProduct { Id = "1", Code = "ABC", Name = "Test" };
        var validator = new FieldUniqueValidator<TestProduct>(entity, x => x.Code);
        var errs = new Errs();

        await validator.ValidateAsync(context, errs, false, CancellationToken.None);

        errs.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task SingleField_DuplicateFound_ContainsErrorWithCodeUNIQUE()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Existing" });
        await context.SaveChangesAsync();

        var entity = new TestProduct { Id = "2", Code = "ABC", Name = "New" };
        var validator = new FieldUniqueValidator<TestProduct>(entity, x => x.Code);
        var errs = new Errs();

        await validator.ValidateAsync(context, errs, false, CancellationToken.None);

        errs.IsValid.Should().BeFalse();
        errs.Entries.Should().ContainSingle();
        errs.Entries[0].Code.Should().Be("UNIQUE");
        errs.Entries[0].Key.Should().Be("Code");
    }

    [Fact]
    public async Task MultipleFields_AllUnique_NoErrors()
    {
        await using var context = CreateContext();
        var entity = new TestProduct { Id = "1", Code = "ABC", Name = "Unique" };
        var validator = new FieldUniqueValidator<TestProduct>(entity, x => x.Code, x => x.Name);
        var errs = new Errs();

        await validator.ValidateAsync(context, errs, false, CancellationToken.None);

        errs.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task MultipleFields_FirstFieldDuplicate_ContainsError()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Other" });
        await context.SaveChangesAsync();

        var entity = new TestProduct { Id = "2", Code = "ABC", Name = "Unique" };
        var validator = new FieldUniqueValidator<TestProduct>(entity, x => x.Code, x => x.Name);
        var errs = new Errs();

        await validator.ValidateAsync(context, errs, false, CancellationToken.None);

        errs.IsValid.Should().BeFalse();
        errs.Entries.Should().ContainSingle();
        errs.Entries[0].Key.Should().Be("Code");
        errs.Entries[0].Code.Should().Be("UNIQUE");
    }

    [Fact]
    public async Task IgnoreSelfId_WhenDuplicateIsSelf_NoError()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Test" });
        await context.SaveChangesAsync();

        var entity = new TestProduct { Id = "1", Code = "ABC", Name = "Test" };
        var validator = new FieldUniqueValidator<TestProduct>(entity, x => x.Code);
        var errs = new Errs();

        await validator.ValidateAsync(context, errs, true, CancellationToken.None);

        errs.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task IgnoreSelfId_WhenDuplicateIsOther_ContainsError()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Existing" });
        await context.SaveChangesAsync();

        var entity = new TestProduct { Id = "2", Code = "ABC", Name = "New" };
        var validator = new FieldUniqueValidator<TestProduct>(entity, x => x.Code);
        var errs = new Errs();

        await validator.ValidateAsync(context, errs, true, CancellationToken.None);

        errs.IsValid.Should().BeFalse();
        errs.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task IgnoreSelfIdFalse_WhenDuplicateIsSelf_ContainsError()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Test" });
        await context.SaveChangesAsync();

        var entity = new TestProduct { Id = "1", Code = "ABC", Name = "Test" };
        var validator = new FieldUniqueValidator<TestProduct>(entity, x => x.Code);
        var errs = new Errs();

        await validator.ValidateAsync(context, errs, false, CancellationToken.None);

        errs.IsValid.Should().BeFalse();
        errs.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task NullFieldValue_DoesNotThrowException()
    {
        await using var context = CreateContext();
        var entity = new TestProduct { Id = "1", Code = null!, Name = "Test" };
        var validator = new FieldUniqueValidator<TestProduct>(entity, x => x.Code);
        var errs = new Errs();

        var act = async () => await validator.ValidateAsync(context, errs, false, CancellationToken.None);

        await act.Should().NotThrowAsync();
        errs.IsValid.Should().BeTrue();
    }
}
