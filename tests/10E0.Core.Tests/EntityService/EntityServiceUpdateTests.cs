using TenE0.Core.Abstractions;
using TenE0.Core.EntityService;
using TenE0.Core.EntityService.Validators;
using EntitySvc = TenE0.Core.EntityService.EntityService;
using TenE0.Core.Errors;
using TenE0.Core.Permissions;
using TenE0.Core.Sequences;

namespace TenE0.Core.Tests.EntityService;

[Trait("Category", "Unit")]
public sealed class EntityServiceUpdateTests
{
    private sealed class TestProduct : IBaseEntity
    {
        public string Id { get; set; } = "";
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private sealed class TestTimedProduct : ITimerEntity
    {
        public string Id { get; set; } = "";
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTimeOffset? CreateTime { get; set; }
        public string? CreateBy { get; set; }
        public DateTimeOffset? UpdateTime { get; set; }
        public string? UpdateBy { get; set; }
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TestProduct> Products => Set<TestProduct>();
        public DbSet<TestTimedProduct> TimedProducts => Set<TestTimedProduct>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestProduct>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Code).IsRequired(false);
            });

            modelBuilder.Entity<TestTimedProduct>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Code).IsRequired(false);
                entity.Property(e => e.CreateTime).IsRequired(false);
                entity.Property(e => e.UpdateTime).IsRequired(false);
            });
        }
    }

    private static TestDbContext CreateContext()
    {
        return CreateContext(Guid.NewGuid().ToString());
    }

    private static TestDbContext CreateContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new TestDbContext(options);
    }

    private static Mock<IPermissionEvaluator> CreatePermissionMock(bool defaultResult = true)
    {
        var mock = new Mock<IPermissionEvaluator>();
        mock.Setup(p => p.HasAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(defaultResult);
        return mock;
    }

    // ══════════════════════════════════════════════════════════════
    //  1. Full update — all scalar fields
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FullUpdate_SavesAllScalarFields()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Original" });
        await context.SaveChangesAsync();

        var errs = new Errs();
        var permissionMock = CreatePermissionMock();
        var sut = new EntitySvc(errs, permissionMock.Object);
        var updated = new TestProduct { Id = "1", Code = "DEF", Name = "Updated" };

        var result = await sut.UpdateAsync(context, updated);

        result.Should().BeTrue();
        var saved = await context.Products.AsNoTracking().FirstOrDefaultAsync(e => e.Id == "1");
        saved.Should().NotBeNull();
        saved!.Code.Should().Be("DEF");
        saved.Name.Should().Be("Updated");
    }

    // ══════════════════════════════════════════════════════════════
    //  2. Partial update — only posted properties
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PartialUpdate_PostedProperties()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Original" });
        await context.SaveChangesAsync();

        var errs = new Errs();
        var permissionMock = CreatePermissionMock();
        var sut = new EntitySvc(errs, permissionMock.Object);
        var updated = new TestProduct { Id = "1", Code = "DEF", Name = "NewName" };
        var options = new EntityWriteOptions
        {
            PostedProperties = new HashSet<string> { "Name" }
        };

        var result = await sut.UpdateAsync(context, updated, options);

        result.Should().BeTrue();
        var saved = await context.Products.AsNoTracking().FirstOrDefaultAsync(e => e.Id == "1");
        saved.Should().NotBeNull();
        saved!.Name.Should().Be("NewName");
        // Code should remain unchanged — only "Name" was posted
        saved.Code.Should().Be("ABC");
    }

    // ══════════════════════════════════════════════════════════════
    //  3. Entity not found
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task EntityNotFound_ReturnsFalse()
    {
        await using var context = CreateContext();
        var errs = new Errs();
        var permissionMock = CreatePermissionMock();
        var sut = new EntitySvc(errs, permissionMock.Object);
        var entity = new TestProduct { Id = "999", Code = "GHI", Name = "Ghost" };

        var result = await sut.UpdateAsync(context, entity);

        result.Should().BeFalse();
        errs.Entries.Should().Contain(e => e.Code == "NOT_FOUND");
        errs.Entries.Should().Contain(e => e.Key == nameof(TestProduct.Id));
    }

    // ══════════════════════════════════════════════════════════════
    //  4-5. Unique validators (Update — ignoreSelfId=true)
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task UniqueValidator_IgnoreSelfId()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Original" });
        await context.SaveChangesAsync();

        var errs = new Errs();
        var permissionMock = CreatePermissionMock();
        var validatorMock = new Mock<IUniqueValidator>();
        validatorMock.Setup(v => v.ValidateAsync(
                It.IsAny<DbContext>(), It.IsAny<IErrs>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new EntitySvc(errs, permissionMock.Object);
        var updated = new TestProduct { Id = "1", Code = "DEF", Name = "Updated" };
        var options = new EntityWriteOptions
        {
            UniqueValidators = [validatorMock.Object]
        };

        var result = await sut.UpdateAsync(context, updated, options);

        result.Should().BeTrue();
        // Update calls UniqueValidator with ignoreSelfId=true
        validatorMock.Verify(
            v => v.ValidateAsync(context, errs, true, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UniqueValidator_Fails_ReturnsFalse()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Original" });
        await context.SaveChangesAsync();

        var errs = new Errs();
        var permissionMock = CreatePermissionMock();
        var validatorMock = new Mock<IUniqueValidator>();
        validatorMock.Setup(v => v.ValidateAsync(
                It.IsAny<DbContext>(), It.IsAny<IErrs>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<DbContext, IErrs, bool, CancellationToken>((_, e, _, _) =>
                e.Add("Duplicate Code", key: "Code", code: "UNIQUE"))
            .Returns(Task.CompletedTask);

        var sut = new EntitySvc(errs, permissionMock.Object);
        var updated = new TestProduct { Id = "1", Code = "ABC", Name = "Updated" };
        var options = new EntityWriteOptions
        {
            UniqueValidators = [validatorMock.Object]
        };

        var result = await sut.UpdateAsync(context, updated, options);

        result.Should().BeFalse();
        errs.Entries.Should().Contain(e => e.Code == "UNIQUE");
    }

    // ══════════════════════════════════════════════════════════════
    //  6-9. Field permissions (Update scenario)
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FieldPermissions_Pass()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Original" });
        await context.SaveChangesAsync();

        var errs = new Errs();
        var permissionMock = new Mock<IPermissionEvaluator>();
        permissionMock.Setup(p => p.HasAsync("product.write", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = new EntitySvc(errs, permissionMock.Object);
        var updated = new TestProduct { Id = "1", Code = "DEF", Name = "Updated" };
        var options = new EntityWriteOptions
        {
            FieldPermissions = new Dictionary<string, string> { ["Code"] = "product.write" }
        };

        var result = await sut.UpdateAsync(context, updated, options);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task FieldPermissions_Fail()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Original" });
        await context.SaveChangesAsync();

        var errs = new Errs();
        var permissionMock = new Mock<IPermissionEvaluator>();
        permissionMock.Setup(p => p.HasAsync("product.write", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var sut = new EntitySvc(errs, permissionMock.Object);
        var updated = new TestProduct { Id = "1", Code = "DEF", Name = "Updated" };
        var options = new EntityWriteOptions
        {
            FieldPermissions = new Dictionary<string, string> { ["Code"] = "product.write" }
        };

        var result = await sut.UpdateAsync(context, updated, options);

        result.Should().BeFalse();
        errs.Entries.Should().Contain(e => e.Code == "FIELD_PERM");
    }

    [Fact]
    public async Task FieldPermissions_NarrowedByPostedProperties()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Original" });
        await context.SaveChangesAsync();

        var errs = new Errs();
        var permissionMock = new Mock<IPermissionEvaluator>();
        permissionMock.Setup(p => p.HasAsync("code.write", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        permissionMock.Setup(p => p.HasAsync("name.write", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = new EntitySvc(errs, permissionMock.Object);
        var updated = new TestProduct { Id = "1", Code = "DEF", Name = "Updated" };
        var options = new EntityWriteOptions
        {
            PostedProperties = new HashSet<string> { "Code" },
            FieldPermissions = new Dictionary<string, string>
            {
                ["Code"] = "code.write",
                ["Name"] = "name.write"
            }
        };

        var result = await sut.UpdateAsync(context, updated, options);

        result.Should().BeFalse();
        errs.Entries.Should().Contain(e => e.Code == "FIELD_PERM");
        // Only "Code" should be checked — "Name" is not in PostedProperties
        permissionMock.Verify(p => p.HasAsync("code.write", It.IsAny<CancellationToken>()), Times.Once);
        permissionMock.Verify(p => p.HasAsync("name.write", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FieldPermissions_AllFields_WhenPostedNull()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Original" });
        await context.SaveChangesAsync();

        var errs = new Errs();
        var permissionMock = new Mock<IPermissionEvaluator>();
        permissionMock.Setup(p => p.HasAsync("code.write", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        permissionMock.Setup(p => p.HasAsync("name.write", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var sut = new EntitySvc(errs, permissionMock.Object);
        var updated = new TestProduct { Id = "1", Code = "DEF", Name = "Updated" };
        var options = new EntityWriteOptions
        {
            // PostedProperties = null means all controlled fields are checked
            FieldPermissions = new Dictionary<string, string>
            {
                ["Code"] = "code.write",
                ["Name"] = "name.write"
            }
        };

        var result = await sut.UpdateAsync(context, updated, options);

        result.Should().BeFalse();
        // Both fields should be checked since PostedProperties is null
        permissionMock.Verify(p => p.HasAsync("code.write", It.IsAny<CancellationToken>()), Times.Once);
        permissionMock.Verify(p => p.HasAsync("name.write", It.IsAny<CancellationToken>()), Times.Once);
        errs.Entries.Should().Contain(e => e.Code == "FIELD_PERM");
    }

    // ══════════════════════════════════════════════════════════════
    //  10-11. BeforeSave hook (Update)
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task BeforeSave_Hook_Called()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Original" });
        await context.SaveChangesAsync();

        var errs = new Errs();
        var permissionMock = CreatePermissionMock();
        var hookCalled = false;

        var sut = new EntitySvc(errs, permissionMock.Object);
        var updated = new TestProduct { Id = "1", Code = "DEF", Name = "Updated" };
        var options = new EntityWriteOptions
        {
            BeforeSaveAsync = async (ct) => { hookCalled = true; }
        };

        var result = await sut.UpdateAsync(context, updated, options);

        result.Should().BeTrue();
        hookCalled.Should().BeTrue();
    }

    [Fact]
    public async Task BeforeSave_HookInvalid_ReturnsFalse()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Original" });
        await context.SaveChangesAsync();

        var errs = new Errs();
        var permissionMock = CreatePermissionMock();

        var sut = new EntitySvc(errs, permissionMock.Object);
        var updated = new TestProduct { Id = "1", Code = "DEF", Name = "Updated" };
        var options = new EntityWriteOptions
        {
            BeforeSaveAsync = async (ct) => { errs.Add("Hook error"); }
        };

        var result = await sut.UpdateAsync(context, updated, options);

        result.Should().BeFalse();
        // Entity should NOT be updated in DB
        var saved = await context.Products.AsNoTracking().FirstOrDefaultAsync(e => e.Id == "1");
        saved.Should().NotBeNull();
        saved!.Name.Should().Be("Original");
    }

    // ══════════════════════════════════════════════════════════════
    //  12. Audit fields — never patched
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task AuditFields_NeverPatched()
    {
        var dbName = Guid.NewGuid().ToString();
        DateTimeOffset originalTime = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Seed with audit values in a separate context
        {
            await using var seedCtx = CreateContext(dbName);
            seedCtx.TimedProducts.Add(new TestTimedProduct
            {
                Id = "1",
                Code = "ABC",
                Name = "Original",
                CreateTime = originalTime,
                CreateBy = "creator",
                UpdateTime = originalTime,
                UpdateBy = "updater"
            });
            await seedCtx.SaveChangesAsync();
        }

        // Update with different audit values — they should be ignored
        await using var context = CreateContext(dbName);
        var errs = new Errs();
        var permissionMock = CreatePermissionMock();
        var sut = new EntitySvc(errs, permissionMock.Object);
        var updated = new TestTimedProduct
        {
            Id = "1",
            Code = "DEF",
            Name = "Updated",
            CreateTime = DateTimeOffset.MaxValue,
            CreateBy = "hacker",
            UpdateTime = DateTimeOffset.MaxValue,
            UpdateBy = "hacker"
        };

        var result = await sut.UpdateAsync(context, updated);

        result.Should().BeTrue();
        var saved = await context.TimedProducts.AsNoTracking().FirstOrDefaultAsync(e => e.Id == "1");
        saved.Should().NotBeNull();
        saved!.Code.Should().Be("DEF");
        saved.Name.Should().Be("Updated");
        // Audit fields must remain unchanged
        saved.CreateTime.Should().Be(originalTime);
        saved.CreateBy.Should().Be("creator");
        saved.UpdateTime.Should().Be(originalTime);
        saved.UpdateBy.Should().Be("updater");
    }

    // ══════════════════════════════════════════════════════════════
    //  13. PostedNavigations null — no diff
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PostedNavigations_IsNull_NoDiff()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Original" });
        await context.SaveChangesAsync();

        var errs = new Errs();
        var permissionMock = CreatePermissionMock();
        var sut = new EntitySvc(errs, permissionMock.Object);
        var updated = new TestProduct { Id = "1", Code = "DEF", Name = "Updated" };
        // PostedNavigations defaults to null
        var options = new EntityWriteOptions
        {
            PostedNavigations = null
        };

        var result = await sut.UpdateAsync(context, updated, options);

        result.Should().BeTrue();
    }

    // ══════════════════════════════════════════════════════════════
    //  14. Scalar fields — only posted properties patched
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ScalarFields_OnlyPatchedFields()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Original", });
        await context.SaveChangesAsync();

        var errs = new Errs();
        var permissionMock = CreatePermissionMock();
        var sut = new EntitySvc(errs, permissionMock.Object);
        var updated = new TestProduct { Id = "1", Code = "SHOULD_NOT_PATCH", Name = "NameChanged" };
        var options = new EntityWriteOptions
        {
            PostedProperties = new HashSet<string> { "Name" }
        };

        var result = await sut.UpdateAsync(context, updated, options);

        result.Should().BeTrue();
        var saved = await context.Products.AsNoTracking().FirstOrDefaultAsync(e => e.Id == "1");
        saved.Should().NotBeNull();
        saved!.Name.Should().Be("NameChanged");
        // Code must remain unchanged — it was not in PostedProperties
        saved.Code.Should().Be("ABC");
    }

    // ══════════════════════════════════════════════════════════════
    //  15. Full check — FieldPermissions + BeforeSave combined
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FullCheck_FieldPermissions_And_BeforeSave()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "ABC", Name = "Original" });
        await context.SaveChangesAsync();

        var errs = new Errs();
        var permissionMock = new Mock<IPermissionEvaluator>();
        permissionMock.Setup(p => p.HasAsync("code.write", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var hookCalled = false;

        var sut = new EntitySvc(errs, permissionMock.Object);
        var updated = new TestProduct { Id = "1", Code = "DEF", Name = "Combined" };
        var options = new EntityWriteOptions
        {
            PostedProperties = new HashSet<string> { "Code", "Name" },
            FieldPermissions = new Dictionary<string, string> { ["Code"] = "code.write" },
            UniqueValidators = [],
            BeforeSaveAsync = async (ct) => { hookCalled = true; }
        };

        var result = await sut.UpdateAsync(context, updated, options);

        result.Should().BeTrue();
        hookCalled.Should().BeTrue();
        permissionMock.Verify(p => p.HasAsync("code.write", It.IsAny<CancellationToken>()), Times.Once);
        var saved = await context.Products.AsNoTracking().FirstOrDefaultAsync(e => e.Id == "1");
        saved.Should().NotBeNull();
        saved!.Code.Should().Be("DEF");
        saved.Name.Should().Be("Combined");
    }

    // ══════════════════════════════════════════════════════════════
    //  16. Code field patched correctly
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CodeField_PatchedCorrectly()
    {
        await using var context = CreateContext();
        context.Products.Add(new TestProduct { Id = "1", Code = "OLD_CODE", Name = "Test" });
        await context.SaveChangesAsync();

        var errs = new Errs();
        var permissionMock = CreatePermissionMock();
        var sut = new EntitySvc(errs, permissionMock.Object);
        var updated = new TestProduct { Id = "1", Code = "NEW_CODE", Name = "Test" };

        var result = await sut.UpdateAsync(context, updated);

        result.Should().BeTrue();
        var saved = await context.Products.AsNoTracking().FirstOrDefaultAsync(e => e.Id == "1");
        saved.Should().NotBeNull();
        saved!.Code.Should().Be("NEW_CODE");
    }
}
