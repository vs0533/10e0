using TenE0.Core.Abstractions;
using TenE0.Core.EntityService.Relations;
using Microsoft.EntityFrameworkCore.Metadata;

namespace TenE0.Core.Tests.EntityService.Relations;

[Trait("Category", "Unit")]
public sealed class RelationProcessorTests
{
    // ── Test Entities ──────────────────────────────────────────────

    private sealed class Tag : IBaseEntity
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public ICollection<TestArticle> Articles { get; set; } = [];
    }

    private sealed class TestArticle : IBaseEntity
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";

        // Regular navigation (many-to-one) — should be CLEARED by CleanNavigations
        public Category? Category { get; set; }
        public string CategoryId { get; set; } = "";

        // Skip navigation (M:N to Tags) — should be PRESERVED by CleanNavigations
        public ICollection<Tag> Tags { get; set; } = [];
    }

    private sealed class Category : IBaseEntity
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    // ── Test DbContext ─────────────────────────────────────────────

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TestArticle> Articles => Set<TestArticle>();
        public DbSet<Tag> Tags => Set<Tag>();
        public DbSet<Category> Categories => Set<Category>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestArticle>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasOne(e => e.Category)
                    .WithMany()
                    .HasForeignKey(e => e.CategoryId);
                entity.HasMany(e => e.Tags)
                    .WithMany(e => e.Articles);
            });

            modelBuilder.Entity<Tag>(entity => entity.HasKey(e => e.Id));
            modelBuilder.Entity<Category>(entity => entity.HasKey(e => e.Id));
        }
    }

    // ── Helpers ────────────────────────────────────────────────────

    private static TestDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new TestDbContext(options);
    }

    private static Tag MakeTag(string id, string name) => new() { Id = id, Name = name };

    // ══════════════════════════════════════════════════════════════
    //  CleanNavigations  (≈6 tests)
    // ══════════════════════════════════════════════════════════════

    // ── 1. Clears regular navigation ─────────────────────────────

    [Fact]
    public void CleanNavigations_ClearsRegularNavigation_WhenEntityHasOne()
    {
        using var db = CreateDbContext(nameof(CleanNavigations_ClearsRegularNavigation_WhenEntityHasOne));
        var category = new Category { Id = "c1", Name = "Tech" };
        var article = new TestArticle
        {
            Id = "a1",
            Title = "Test",
            Category = category,
            CategoryId = "c1",
            Tags = [MakeTag("t1", "dotnet")]
        };

        RelationProcessor.CleanNavigations(db, article);

        article.Category.Should().BeNull();
    }

    // ── 2. Preserves skip navigation ─────────────────────────────

    [Fact]
    public void CleanNavigations_PreservesSkipNavigation_WhenEntityHasManyToMany()
    {
        using var db = CreateDbContext(nameof(CleanNavigations_PreservesSkipNavigation_WhenEntityHasManyToMany));
        var tags = new List<Tag> { MakeTag("t1", "dotnet"), MakeTag("t2", "csharp") };
        var article = new TestArticle
        {
            Id = "a1",
            Title = "Test",
            Category = new Category { Id = "c1", Name = "Tech" },
            CategoryId = "c1",
            Tags = tags
        };

        RelationProcessor.CleanNavigations(db, article);

        article.Tags.Should().HaveCount(2);
        article.Tags.Should().Contain(t => t.Id == "t1");
        article.Tags.Should().Contain(t => t.Id == "t2");
    }

    // ── 3. Entity type not registered ────────────────────────────

    [Fact]
    public void CleanNavigations_EntityTypeNotRegistered_ThrowsInvalidOperationException()
    {
        var options = new DbContextOptionsBuilder<DbContext>()
            .UseInMemoryDatabase(nameof(CleanNavigations_EntityTypeNotRegistered_ThrowsInvalidOperationException))
            .Options;
        using var db = new DbContext(options);
        var article = new TestArticle { Id = "a1", Title = "Test" };

        var act = () => RelationProcessor.CleanNavigations(db, article);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("实体类型未在 DbContext 中注册：TestArticle");
    }

    // ── 4. Multiple regular navigations → all cleared ────────────

    [Fact]
    public void CleanNavigations_ClearsRegularAndPreservesSkip_WhenBothExist()
    {
        using var db = CreateDbContext(nameof(CleanNavigations_ClearsRegularAndPreservesSkip_WhenBothExist));
        var article = new TestArticle
        {
            Id = "a1",
            Title = "Test",
            Category = new Category { Id = "c1", Name = "Tech" },
            CategoryId = "c1",
            Tags = [MakeTag("t1", "dotnet")]
        };

        RelationProcessor.CleanNavigations(db, article);

        article.Category.Should().BeNull();
        article.Tags.Should().ContainSingle(t => t.Id == "t1");
    }

    // ── 5. Null regular navigation → no-op ───────────────────────

    [Fact]
    public void CleanNavigations_NullRegularNavigation_DoesNotThrow()
    {
        using var db = CreateDbContext(nameof(CleanNavigations_NullRegularNavigation_DoesNotThrow));
        var article = new TestArticle
        {
            Id = "a1",
            Title = "Test",
            Category = null,
            CategoryId = "c1",
            Tags = [MakeTag("t1", "dotnet")]
        };

        RelationProcessor.CleanNavigations(db, article);

        article.Category.Should().BeNull();
        article.Tags.Should().ContainSingle(t => t.Id == "t1");
    }

    // ── 6. Entity with no skip navigations → no crash ────────────

    [Fact]
    public void CleanNavigations_EntityWithNoSkipNavigations_DoesNotThrow()
    {
        using var db = CreateDbContext(nameof(CleanNavigations_EntityWithNoSkipNavigations_DoesNotThrow));
        var category = new Category { Id = "c1", Name = "Tech" };

        var act = () => RelationProcessor.CleanNavigations(db, category);

        act.Should().NotThrow();
    }

    // ══════════════════════════════════════════════════════════════
    //  DiffSkipNavigations  (≈8 tests)
    // ══════════════════════════════════════════════════════════════

    private static IEntityType GetArticleEntityType(string dbName)
    {
        using var db = CreateDbContext(dbName);
        // Force model creation by accessing .Model
        return db.Model.FindEntityType(typeof(TestArticle))!;
    }

    // ── 1. Add new items ─────────────────────────────────────────

    [Fact]
    public void DiffSkipNavigations_AddsNewItems_WhenPostedHasMore()
    {
        var entityType = GetArticleEntityType(nameof(DiffSkipNavigations_AddsNewItems_WhenPostedHasMore));
        var t1 = MakeTag("t1", "dotnet");
        var t2 = MakeTag("t2", "csharp");

        var dbArticle = new TestArticle { Id = "a1", Title = "Test", Tags = new List<Tag> { t1 } };
        var postedArticle = new TestArticle { Id = "a1", Title = "Test", Tags = new List<Tag> { t1, t2 } };

        RelationProcessor.DiffSkipNavigations(entityType, dbArticle, postedArticle);

        dbArticle.Tags.Should().HaveCount(2);
        dbArticle.Tags.Should().Contain(t => t.Id == "t1");
        dbArticle.Tags.Should().Contain(t => t.Id == "t2");
    }

    // ── 2. Remove items ──────────────────────────────────────────

    [Fact]
    public void DiffSkipNavigations_RemovesItems_WhenPostedHasFewer()
    {
        var entityType = GetArticleEntityType(nameof(DiffSkipNavigations_RemovesItems_WhenPostedHasFewer));
        var t1 = MakeTag("t1", "dotnet");
        var t2 = MakeTag("t2", "csharp");

        var dbArticle = new TestArticle { Id = "a1", Title = "Test", Tags = new List<Tag> { t1, t2 } };
        var postedArticle = new TestArticle { Id = "a1", Title = "Test", Tags = new List<Tag> { t1 } };

        RelationProcessor.DiffSkipNavigations(entityType, dbArticle, postedArticle);

        dbArticle.Tags.Should().ContainSingle(t => t.Id == "t1");
    }

    // ── 3. Keep unchanged ────────────────────────────────────────

    [Fact]
    public void DiffSkipNavigations_KeepsUnchanged_WhenSame()
    {
        var entityType = GetArticleEntityType(nameof(DiffSkipNavigations_KeepsUnchanged_WhenSame));
        var t1 = MakeTag("t1", "dotnet");
        var t2 = MakeTag("t2", "csharp");

        var dbArticle = new TestArticle { Id = "a1", Title = "Test", Tags = new List<Tag> { t1, t2 } };
        var postedArticle = new TestArticle { Id = "a1", Title = "Test", Tags = new List<Tag> { t1, t2 } };

        RelationProcessor.DiffSkipNavigations(entityType, dbArticle, postedArticle);

        dbArticle.Tags.Should().HaveCount(2);
        dbArticle.Tags.Should().Contain(t => t.Id == "t1");
        dbArticle.Tags.Should().Contain(t => t.Id == "t2");
    }

    // ── 4. Full replacement ──────────────────────────────────────

    [Fact]
    public void DiffSkipNavigations_FullReplacement_WhenItemsDifferent()
    {
        var entityType = GetArticleEntityType(nameof(DiffSkipNavigations_FullReplacement_WhenItemsDifferent));
        var t1 = MakeTag("t1", "dotnet");
        var t2 = MakeTag("t2", "csharp");

        var dbArticle = new TestArticle { Id = "a1", Title = "Test", Tags = new List<Tag> { t1 } };
        var postedArticle = new TestArticle { Id = "a1", Title = "Test", Tags = new List<Tag> { t2 } };

        RelationProcessor.DiffSkipNavigations(entityType, dbArticle, postedArticle);

        dbArticle.Tags.Should().ContainSingle(t => t.Id == "t2");
    }

    // ── 5. processOnly filter excludes nav ───────────────────────

    [Fact]
    public void DiffSkipNavigations_DoesNotProcess_WhenNavigationNotInProcessOnly()
    {
        var entityType = GetArticleEntityType(nameof(DiffSkipNavigations_DoesNotProcess_WhenNavigationNotInProcessOnly));
        var t1 = MakeTag("t1", "dotnet");
        var t2 = MakeTag("t2", "csharp");
        var processOnly = new HashSet<string> { "OtherNav" };

        var dbArticle = new TestArticle { Id = "a1", Title = "Test", Tags = new List<Tag> { t1 } };
        var postedArticle = new TestArticle { Id = "a1", Title = "Test", Tags = new List<Tag> { t1, t2 } };

        RelationProcessor.DiffSkipNavigations(entityType, dbArticle, postedArticle, processOnly);

        dbArticle.Tags.Should().ContainSingle(t => t.Id == "t1");
    }

    // ── 6. processOnly includes nav ──────────────────────────────

    [Fact]
    public void DiffSkipNavigations_Processes_WhenNavigationInProcessOnly()
    {
        var entityType = GetArticleEntityType(nameof(DiffSkipNavigations_Processes_WhenNavigationInProcessOnly));
        var t1 = MakeTag("t1", "dotnet");
        var t2 = MakeTag("t2", "csharp");
        var processOnly = new HashSet<string> { "Tags" };

        var dbArticle = new TestArticle { Id = "a1", Title = "Test", Tags = new List<Tag> { t1 } };
        var postedArticle = new TestArticle { Id = "a1", Title = "Test", Tags = new List<Tag> { t1, t2 } };

        RelationProcessor.DiffSkipNavigations(entityType, dbArticle, postedArticle, processOnly);

        dbArticle.Tags.Should().HaveCount(2);
        dbArticle.Tags.Should().Contain(t => t.Id == "t1");
        dbArticle.Tags.Should().Contain(t => t.Id == "t2");
    }

    // ── 7. Posted nav is null ────────────────────────────────────

    [Fact]
    public void DiffSkipNavigations_PostedNavigationNull_DoesNotChange()
    {
        var entityType = GetArticleEntityType(nameof(DiffSkipNavigations_PostedNavigationNull_DoesNotChange));
        var t1 = MakeTag("t1", "dotnet");

        var dbArticle = new TestArticle { Id = "a1", Title = "Test", Tags = new List<Tag> { t1 } };
        var postedArticle = new TestArticle { Id = "a1", Title = "Test", Tags = null! };

        RelationProcessor.DiffSkipNavigations(entityType, dbArticle, postedArticle);

        dbArticle.Tags.Should().ContainSingle(t => t.Id == "t1");
    }

    // ── 8. Empty posted collection ───────────────────────────────

    [Fact]
    public void DiffSkipNavigations_EmptyPostedCollection_RemovesAll()
    {
        var entityType = GetArticleEntityType(nameof(DiffSkipNavigations_EmptyPostedCollection_RemovesAll));
        var t1 = MakeTag("t1", "dotnet");
        var t2 = MakeTag("t2", "csharp");

        var dbArticle = new TestArticle { Id = "a1", Title = "Test", Tags = new List<Tag> { t1, t2 } };
        var postedArticle = new TestArticle { Id = "a1", Title = "Test", Tags = new List<Tag>() };

        RelationProcessor.DiffSkipNavigations(entityType, dbArticle, postedArticle);

        dbArticle.Tags.Should().BeEmpty();
    }
}
