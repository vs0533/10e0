using TenE0.Core.Abstractions;
using TenE0.Core.Errors;
using TenE0.Core.EntityService.Validators;

namespace TenE0.Core.Tests.EntityService.Validators;

[Trait("Category", "Unit")]
public sealed class GroupUniqueValidatorTests
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
    public async Task CombinedMatch_WhenAllFieldsMatch_ContainsErrorWithCodeUNIQUE_GROUP()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Test" });
        await context.SaveChangesAsync();

        var entity = new TestProduct { Id = "2", Code = "ABC", Name = "Test" };
        var validator = new GroupUniqueValidator<TestProduct>(entity, x => x.Code, x => x.Name);
        var errs = new Errs();

        await validator.ValidateAsync(context, errs, false, CancellationToken.None);

        errs.IsValid.Should().BeFalse();
        errs.Entries.Should().ContainSingle();
        errs.Entries[0].Code.Should().Be("UNIQUE_GROUP");
    }

    [Fact]
    public async Task CombinedNoMatch_WhenOnlyPartialMatch_ReturnsValid()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Test" });
        await context.SaveChangesAsync();

        var entity = new TestProduct { Id = "2", Code = "ABC", Name = "Other" };
        var validator = new GroupUniqueValidator<TestProduct>(entity, x => x.Code, x => x.Name);
        var errs = new Errs();

        await validator.ValidateAsync(context, errs, false, CancellationToken.None);

        errs.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task IgnoreSelfId_WhenMatchIsSelf_NoError()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Test" });
        await context.SaveChangesAsync();

        var entity = new TestProduct { Id = "1", Code = "ABC", Name = "Test" };
        var validator = new GroupUniqueValidator<TestProduct>(entity, x => x.Code, x => x.Name);
        var errs = new Errs();

        await validator.ValidateAsync(context, errs, true, CancellationToken.None);

        errs.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task IgnoreSelfId_WhenMatchIsOther_ContainsError()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Test" });
        await context.SaveChangesAsync();

        var entity = new TestProduct { Id = "2", Code = "ABC", Name = "Test" };
        var validator = new GroupUniqueValidator<TestProduct>(entity, x => x.Code, x => x.Name);
        var errs = new Errs();

        await validator.ValidateAsync(context, errs, true, CancellationToken.None);

        errs.IsValid.Should().BeFalse();
        errs.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task SingleFieldGroup_WithMatch_ContainsError()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Test" });
        await context.SaveChangesAsync();

        var entity = new TestProduct { Id = "2", Code = "ABC", Name = "Other" };
        var validator = new GroupUniqueValidator<TestProduct>(entity, x => x.Code);
        var errs = new Errs();

        await validator.ValidateAsync(context, errs, false, CancellationToken.None);

        errs.IsValid.Should().BeFalse();
        errs.Entries.Should().ContainSingle();
        errs.Entries[0].Code.Should().Be("UNIQUE_GROUP");
    }

    [Fact]
    public async Task SingleFieldGroup_NoMatch_ReturnsValid()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Test" });
        await context.SaveChangesAsync();

        var entity = new TestProduct { Id = "2", Code = "XYZ", Name = "Test" };
        var validator = new GroupUniqueValidator<TestProduct>(entity, x => x.Code);
        var errs = new Errs();

        await validator.ValidateAsync(context, errs, false, CancellationToken.None);

        errs.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ZeroSelectors_ThrowsArgumentException()
    {
        var entity = new TestProduct { Id = "1", Code = "ABC", Name = "Test" };

        var act = () => new GroupUniqueValidator<TestProduct>(entity);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task PartialMatch_WhenOnlyOneFieldMatches_NoError()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Test" });
        await context.SaveChangesAsync();

        var entity = new TestProduct { Id = "2", Code = "ABC", Name = "Other" };
        var validator = new GroupUniqueValidator<TestProduct>(entity, x => x.Code, x => x.Name);
        var errs = new Errs();

        await validator.ValidateAsync(context, errs, false, CancellationToken.None);

        errs.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task NullFieldValue_WithMatchingExistingNull_HandlesNullComparison()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = null!, Name = "Test" });
        await context.SaveChangesAsync();

        var entity = new TestProduct { Id = "2", Code = null!, Name = "Test" };
        var validator = new GroupUniqueValidator<TestProduct>(entity, x => x.Code, x => x.Name);
        var errs = new Errs();

        var act = async () => await validator.ValidateAsync(context, errs, false, CancellationToken.None);

        await act.Should().NotThrowAsync();
        errs.IsValid.Should().BeFalse();
        errs.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task EmptyTable_NoSeedData_NoError()
    {
        await using var context = CreateContext();
        var entity = new TestProduct { Id = "1", Code = "ABC", Name = "Test" };
        var validator = new GroupUniqueValidator<TestProduct>(entity, x => x.Code, x => x.Name);
        var errs = new Errs();

        await validator.ValidateAsync(context, errs, false, CancellationToken.None);

        errs.IsValid.Should().BeTrue();
    }
}
