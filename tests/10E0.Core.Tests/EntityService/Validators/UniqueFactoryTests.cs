using TenE0.Core.Abstractions;
using TenE0.Core.Errors;
using TenE0.Core.EntityService.Validators;

namespace TenE0.Core.Tests.EntityService.Validators;

[Trait("Category", "Unit")]
public sealed class UniqueFactoryTests
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
    public void Field_ReturnsFieldUniqueValidator()
    {
        var entity = new TestProduct { Id = "1", Code = "ABC", Name = "Test" };

        var result = Unique.Field<TestProduct>(entity, x => x.Code);

        result.Should().BeOfType<FieldUniqueValidator<TestProduct>>();
    }

    [Fact]
    public void Group_ReturnsGroupUniqueValidator()
    {
        var entity = new TestProduct { Id = "1", Code = "ABC", Name = "Test" };

        var result = Unique.Group<TestProduct>(entity, x => x.Code, x => x.Name);

        result.Should().BeOfType<GroupUniqueValidator<TestProduct>>();
    }

    [Fact]
    public async Task Field_WithThreeSelectors_DuplicateForAnyFieldProducesError()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Existing" });
        await context.SaveChangesAsync();

        var entity = new TestProduct { Id = "2", Code = "ABC", Name = "UniqueName" };
        var validator = Unique.Field<TestProduct>(entity, x => x.Code, x => x.Name, x => x.Id);
        var errs = new Errs();

        await validator.ValidateAsync(context, errs, false, CancellationToken.None);

        errs.IsValid.Should().BeFalse();
        errs.Entries.Should().ContainSingle();
        errs.Entries[0].Key.Should().Be("Code");
    }

    [Fact]
    public async Task Group_WithTwoSelectors_CombinedMatchProducesError()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Test" });
        await context.SaveChangesAsync();

        var entity = new TestProduct { Id = "2", Code = "ABC", Name = "Test" };
        var validator = Unique.Group<TestProduct>(entity, x => x.Code, x => x.Name);
        var errs = new Errs();

        await validator.ValidateAsync(context, errs, false, CancellationToken.None);

        errs.IsValid.Should().BeFalse();
        errs.Entries.Should().ContainSingle();
        errs.Entries[0].Code.Should().Be("UNIQUE_GROUP");
    }

    [Fact]
    public void Field_CanBeAddedToUniqueValidatorsList()
    {
        var entity = new TestProduct { Id = "1", Code = "ABC", Name = "Test" };
        var validator = Unique.Field<TestProduct>(entity, x => x.Code);

        var list = new List<IUniqueValidator> { validator };

        list.Should().ContainSingle().Which.Should().BeSameAs(validator);
    }

    [Fact]
    public void Group_CanBeAddedToUniqueValidatorsList()
    {
        var entity = new TestProduct { Id = "1", Code = "ABC", Name = "Test" };
        var validator = Unique.Group<TestProduct>(entity, x => x.Code, x => x.Name);

        var list = new List<IUniqueValidator> { validator };

        list.Should().ContainSingle().Which.Should().BeSameAs(validator);
    }
}
