using TenE0.Core.Abstractions;
using TenE0.Core.EntityService;
using TenE0.Core.EntityService.Validators;
using EntitySvc = TenE0.Core.EntityService.EntityService;
using TenE0.Core.Errors;
using TenE0.Core.Permissions;
using TenE0.Core.Sequences;

namespace TenE0.Core.Tests.EntityService;

[Trait("Category", "Unit")]
public sealed class EntityServiceCreateTests
{
    private sealed class TestProduct : IBaseEntity
    {
        public string Id { get; set; } = "";
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private sealed class TestSeqProduct : IBaseEntity
    {
        public string Id { get; set; } = "";

        [Sequence("PRD", "PREFIX{SEQ}")]
        public string Code { get; set; } = "";

        public string Name { get; set; } = "";
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TestProduct> Products => Set<TestProduct>();
        public DbSet<TestSeqProduct> SeqProducts => Set<TestSeqProduct>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestProduct>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Code).IsRequired(false);
            });

            modelBuilder.Entity<TestSeqProduct>(entity =>
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
    //  1. Default pipeline — minimal options
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DefaultOptions_SavesEntity_ReturnsTrue()
    {
        await using var context = CreateContext();
        var errs = new Errs();
        var permissionMock = new Mock<IPermissionEvaluator>();
        permissionMock.Setup(p => p.HasAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = new EntitySvc(errs, permissionMock.Object);
        var entity = new TestProduct { Id = "1", Code = "ABC", Name = "Test" };

        var result = await sut.CreateAsync(context, entity);

        result.Should().BeTrue();
        var saved = await context.Products.FirstOrDefaultAsync(e => e.Id == "1");
        saved.Should().NotBeNull();
        saved!.Name.Should().Be("Test");
        saved.Code.Should().Be("ABC");
    }

    // ══════════════════════════════════════════════════════════════
    //  2. KeepNavigationProperties
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task KeepNavigationProperties_True_PreservesNavigations()
    {
        await using var context = CreateContext();
        var errs = new Errs();
        var permissionMock = new Mock<IPermissionEvaluator>();
        permissionMock.Setup(p => p.HasAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = new EntitySvc(errs, permissionMock.Object);
        var entity = new TestProduct { Id = "1", Code = "ABC", Name = "Test" };
        var options = new EntityWriteOptions { KeepNavigationProperties = true };

        var result = await sut.CreateAsync(context, entity, options);

        result.Should().BeTrue();
    }

    // ══════════════════════════════════════════════════════════════
    //  3-4. Field permissions
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FieldPermissions_Pass_SavesEntity()
    {
        await using var context = CreateContext();
        var errs = new Errs();
        var permissionMock = new Mock<IPermissionEvaluator>();
        permissionMock.Setup(p => p.HasAsync("product.write", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = new EntitySvc(errs, permissionMock.Object);
        var entity = new TestProduct { Id = "1", Code = "ABC", Name = "Test" };
        var options = new EntityWriteOptions
        {
            FieldPermissions = new Dictionary<string, string> { ["Code"] = "product.write" }
        };

        var result = await sut.CreateAsync(context, entity, options);

        result.Should().BeTrue();
        var saved = await context.Products.FirstOrDefaultAsync(e => e.Id == "1");
        saved.Should().NotBeNull();
    }

    [Fact]
    public async Task FieldPermissions_Fail_ReturnsFalse()
    {
        await using var context = CreateContext();
        var errs = new Errs();
        var permissionMock = new Mock<IPermissionEvaluator>();
        permissionMock.Setup(p => p.HasAsync("product.write", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var sut = new EntitySvc(errs, permissionMock.Object);
        var entity = new TestProduct { Id = "1", Code = "ABC", Name = "Test" };
        var options = new EntityWriteOptions
        {
            FieldPermissions = new Dictionary<string, string> { ["Code"] = "product.write" }
        };

        var result = await sut.CreateAsync(context, entity, options);

        result.Should().BeFalse();
        errs.Entries.Should().Contain(e => e.Code == "FIELD_PERM");
        // Entity should NOT be saved when permission fails
        var saved = await context.Products.FirstOrDefaultAsync(e => e.Id == "1");
        saved.Should().BeNull();
    }

    // ══════════════════════════════════════════════════════════════
    //  5-7. Sequence generator
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SequenceGenerator_FillsEmptyField()
    {
        await using var context = CreateContext();
        var errs = new Errs();
        var permissionMock = new Mock<IPermissionEvaluator>();
        permissionMock.Setup(p => p.HasAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var sequenceMock = new Mock<ISequenceGenerator>();
        sequenceMock.Setup(s => s.NextAsync("PRD", "PREFIX{SEQ}", It.IsAny<CancellationToken>()))
            .ReturnsAsync("GEN-001");

        var sut = new EntitySvc(errs, permissionMock.Object, sequenceMock.Object);
        var entity = new TestSeqProduct { Id = "1", Code = "", Name = "Test" };

        var result = await sut.CreateAsync(context, entity);

        result.Should().BeTrue();
        entity.Code.Should().Be("GEN-001");
    }

    [Fact]
    public async Task SequenceGenerator_SkipsPreFilledField()
    {
        await using var context = CreateContext();
        var errs = new Errs();
        var permissionMock = new Mock<IPermissionEvaluator>();
        permissionMock.Setup(p => p.HasAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var sequenceMock = new Mock<ISequenceGenerator>(MockBehavior.Strict);

        var sut = new EntitySvc(errs, permissionMock.Object, sequenceMock.Object);
        var entity = new TestSeqProduct { Id = "1", Code = "CUSTOM", Name = "Test" };

        var result = await sut.CreateAsync(context, entity);

        result.Should().BeTrue();
        entity.Code.Should().Be("CUSTOM");
        sequenceMock.Verify(
            s => s.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SequenceGenerator_Null_NoOp()
    {
        await using var context = CreateContext();
        var errs = new Errs();
        var permissionMock = new Mock<IPermissionEvaluator>();
        permissionMock.Setup(p => p.HasAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = new EntitySvc(errs, permissionMock.Object, sequenceGenerator: null);
        var entity = new TestProduct { Id = "1", Code = "", Name = "Test" };

        var result = await sut.CreateAsync(context, entity);

        result.Should().BeTrue();
    }

    // ══════════════════════════════════════════════════════════════
    //  8-9. Unique validators
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task UniqueValidators_Pass_ReturnsTrue()
    {
        await using var context = CreateContext();
        var errs = new Errs();
        var permissionMock = new Mock<IPermissionEvaluator>();
        permissionMock.Setup(p => p.HasAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var validatorMock = new Mock<IUniqueValidator>();
        validatorMock.Setup(v => v.ValidateAsync(
                It.IsAny<DbContext>(), It.IsAny<IErrs>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new EntitySvc(errs, permissionMock.Object);
        var entity = new TestProduct { Id = "1", Code = "ABC", Name = "Test" };
        var options = new EntityWriteOptions
        {
            UniqueValidators = [validatorMock.Object]
        };

        var result = await sut.CreateAsync(context, entity, options);

        result.Should().BeTrue();
        validatorMock.Verify(
            v => v.ValidateAsync(context, errs, false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UniqueValidators_Fail_ReturnsFalse()
    {
        await using var context = CreateContext();
        var errs = new Errs();
        var permissionMock = new Mock<IPermissionEvaluator>();
        permissionMock.Setup(p => p.HasAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var validatorMock = new Mock<IUniqueValidator>();
        validatorMock.Setup(v => v.ValidateAsync(
                It.IsAny<DbContext>(), It.IsAny<IErrs>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<DbContext, IErrs, bool, CancellationToken>((_, e, _, _) =>
                e.Add("Duplicate Code", key: "Code", code: "UNIQUE"))
            .Returns(Task.CompletedTask);

        var sut = new EntitySvc(errs, permissionMock.Object);
        var entity = new TestProduct { Id = "1", Code = "ABC", Name = "Test" };
        var options = new EntityWriteOptions
        {
            UniqueValidators = [validatorMock.Object]
        };

        var result = await sut.CreateAsync(context, entity, options);

        result.Should().BeFalse();
        errs.IsValid.Should().BeFalse();
        errs.Entries.Should().Contain(e => e.Code == "UNIQUE");
    }

    // ══════════════════════════════════════════════════════════════
    //  10-11. BeforeSave hook
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task BeforeSave_Hook_Called()
    {
        await using var context = CreateContext();
        var errs = new Errs();
        var permissionMock = new Mock<IPermissionEvaluator>();
        permissionMock.Setup(p => p.HasAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var hookCalled = false;

        var sut = new EntitySvc(errs, permissionMock.Object);
        var entity = new TestProduct { Id = "1", Code = "ABC", Name = "Test" };
        var options = new EntityWriteOptions
        {
            BeforeSaveAsync = async (ct) => { hookCalled = true; }
        };

        var result = await sut.CreateAsync(context, entity, options);

        result.Should().BeTrue();
        hookCalled.Should().BeTrue();
    }

    [Fact]
    public async Task BeforeSave_HookSetsErrInvalid_ReturnsFalse()
    {
        await using var context = CreateContext();
        var errs = new Errs();
        var permissionMock = new Mock<IPermissionEvaluator>();
        permissionMock.Setup(p => p.HasAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = new EntitySvc(errs, permissionMock.Object);
        var entity = new TestProduct { Id = "1", Code = "ABC", Name = "Test" };
        var options = new EntityWriteOptions
        {
            BeforeSaveAsync = async (ct) => { errs.Add("Hook error"); }
        };

        var result = await sut.CreateAsync(context, entity, options);

        result.Should().BeFalse();
        // Entity should NOT be persisted when hook fails
        var saved = await context.Products.FirstOrDefaultAsync(e => e.Id == "1");
        saved.Should().BeNull();
    }

    // ══════════════════════════════════════════════════════════════
    //  12. Full pipeline — all steps combined
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FullPipeline_Success()
    {
        await using var context = CreateContext();
        var errs = new Errs();
        var permissionMock = new Mock<IPermissionEvaluator>();
        permissionMock.Setup(p => p.HasAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var validatorMock = new Mock<IUniqueValidator>();
        validatorMock.Setup(v => v.ValidateAsync(
                It.IsAny<DbContext>(), It.IsAny<IErrs>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var hookCalled = false;

        var sut = new EntitySvc(errs, permissionMock.Object);
        var entity = new TestProduct { Id = "1", Code = "ABC", Name = "FullPipeline" };
        var options = new EntityWriteOptions
        {
            UniqueValidators = [validatorMock.Object],
            FieldPermissions = new Dictionary<string, string> { ["Code"] = "product.write" },
            BeforeSaveAsync = async (ct) => { hookCalled = true; }
        };

        var result = await sut.CreateAsync(context, entity, options);

        result.Should().BeTrue();
        hookCalled.Should().BeTrue();
        validatorMock.Verify(
            v => v.ValidateAsync(context, errs, false, It.IsAny<CancellationToken>()),
            Times.Once);
        var saved = await context.Products.FirstOrDefaultAsync(e => e.Id == "1");
        saved.Should().NotBeNull();
        saved!.Name.Should().Be("FullPipeline");
        saved.Code.Should().Be("ABC");
    }
}
