using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using TenE0.Core.DynamicFilters;
using TenE0.Core.DynamicFilters.Storage;

namespace TenE0.Core.Tests.DynamicFilters;

[Trait("Category", "Unit")]
public sealed class DataFilterRuleServiceTests
{
    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TenE0DataFilterRule> Rules => Set<TenE0DataFilterRule>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TenE0DataFilterRule>(b => b.HasKey(e => e.Id));
        }
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            optionsBuilder.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<TestDbContext> options) : IDbContextFactory<TestDbContext>
    {
        public TestDbContext CreateDbContext() => new(options);
    }

    private static IDbContextFactory<TestDbContext> CreateFactory(string dbName)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TestDbContextFactory(options);
    }

    private static readonly string ValidRuleJson = """{"logic":"And","rules":[],"children":[]}""";

    [Fact]
    public async Task GetAllAsync_ReturnsAllRulesOrderedByEntityType()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        await using (var seedCtx = factory.CreateDbContext())
        {
            seedCtx.Rules.Add(new TenE0DataFilterRule { EntityTypeName = "Z.Entity", RuleJson = ValidRuleJson });
            seedCtx.Rules.Add(new TenE0DataFilterRule { EntityTypeName = "A.Entity", RuleJson = ValidRuleJson });
            await seedCtx.SaveChangesAsync();
        }

        var svc = new DataFilterRuleService<TestDbContext>(factory, NullLogger<DataFilterRuleService<TestDbContext>>.Instance);

        var rules = await svc.GetAllAsync();

        rules.Should().HaveCount(2);
        rules[0].EntityTypeName.Should().Be("A.Entity");
        rules[1].EntityTypeName.Should().Be("Z.Entity");
    }

    [Fact]
    public async Task GetByEntityAsync_ReturnsMatchingRules()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        await using (var seedCtx = factory.CreateDbContext())
        {
            seedCtx.Rules.Add(new TenE0DataFilterRule { EntityTypeName = "Order", RuleJson = ValidRuleJson });
            seedCtx.Rules.Add(new TenE0DataFilterRule { EntityTypeName = "Order", RuleJson = ValidRuleJson, IsEnabled = false });
            seedCtx.Rules.Add(new TenE0DataFilterRule { EntityTypeName = "Product", RuleJson = ValidRuleJson });
            await seedCtx.SaveChangesAsync();
        }

        var svc = new DataFilterRuleService<TestDbContext>(factory, NullLogger<DataFilterRuleService<TestDbContext>>.Instance);

        var rules = await svc.GetByEntityAsync("Order");

        rules.Should().HaveCount(2);
        rules.Should().AllSatisfy(r => r.EntityTypeName.Should().Be("Order"));
    }

    [Fact]
    public async Task GetByIdAsync_Existing_ReturnsRule()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        string ruleId;
        await using (var seedCtx = factory.CreateDbContext())
        {
            var rule = new TenE0DataFilterRule { EntityTypeName = "Order", RuleJson = ValidRuleJson };
            seedCtx.Rules.Add(rule);
            await seedCtx.SaveChangesAsync();
            ruleId = rule.Id;
        }

        var svc = new DataFilterRuleService<TestDbContext>(factory, NullLogger<DataFilterRuleService<TestDbContext>>.Instance);

        var result = await svc.GetByIdAsync(ruleId);

        result.Should().NotBeNull();
        result!.EntityTypeName.Should().Be("Order");
    }

    [Fact]
    public async Task GetByIdAsync_NonExistent_ReturnsNull()
    {
        var factory = CreateFactory(Guid.NewGuid().ToString("N"));
        var svc = new DataFilterRuleService<TestDbContext>(factory, NullLogger<DataFilterRuleService<TestDbContext>>.Instance);

        var result = await svc.GetByIdAsync("nonexistent");

        result.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_CreatesRuleWithDefaults()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var svc = new DataFilterRuleService<TestDbContext>(factory, NullLogger<DataFilterRuleService<TestDbContext>>.Instance);

        var result = await svc.CreateAsync(new DataFilterRuleCreateRequest("Order", ValidRuleJson, "desc", true));

        result.EntityTypeName.Should().Be("Order");
        result.RuleJson.Should().Be(ValidRuleJson);
        result.Description.Should().Be("desc");
        result.IsEnabled.Should().BeTrue();

        await using var ctx = factory.CreateDbContext();
        var saved = await ctx.Rules.SingleAsync();
        saved.EntityTypeName.Should().Be("Order");
    }

    [Fact]
    public async Task CreateAsync_InvalidJson_ThrowsArgumentException()
    {
        var factory = CreateFactory(Guid.NewGuid().ToString("N"));
        var svc = new DataFilterRuleService<TestDbContext>(factory, NullLogger<DataFilterRuleService<TestDbContext>>.Instance);

        var act = () => svc.CreateAsync(new DataFilterRuleCreateRequest("X", "not json", null));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*ConditionRuleGroup*");
    }

    [Fact]
    public async Task UpdateAsync_Existing_ModifiesFields()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        string ruleId;
        await using (var seedCtx = factory.CreateDbContext())
        {
            var rule = new TenE0DataFilterRule
            {
                EntityTypeName = "Order",
                RuleJson = ValidRuleJson,
                Description = "old",
                IsEnabled = false
            };
            seedCtx.Rules.Add(rule);
            await seedCtx.SaveChangesAsync();
            ruleId = rule.Id;
        }

        var svc = new DataFilterRuleService<TestDbContext>(factory, NullLogger<DataFilterRuleService<TestDbContext>>.Instance);

        await svc.UpdateAsync(ruleId, new DataFilterRuleUpdateRequest(null, "new desc", true));

        await using var ctx = factory.CreateDbContext();
        var updated = await ctx.Rules.FindAsync(ruleId);
        updated!.Description.Should().Be("new desc");
        updated.IsEnabled.Should().BeTrue();
        updated.RuleJson.Should().Be(ValidRuleJson); // unchanged
    }

    [Fact]
    public async Task UpdateAsync_NonExistent_Throws()
    {
        var factory = CreateFactory(Guid.NewGuid().ToString("N"));
        var svc = new DataFilterRuleService<TestDbContext>(factory, NullLogger<DataFilterRuleService<TestDbContext>>.Instance);

        var act = () => svc.UpdateAsync("no-id", new DataFilterRuleUpdateRequest(null, "x", null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*不存在*");
    }

    [Fact]
    public async Task DeleteAsync_RemovesRule()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        string ruleId;
        await using (var seedCtx = factory.CreateDbContext())
        {
            var rule = new TenE0DataFilterRule { EntityTypeName = "X", RuleJson = ValidRuleJson };
            seedCtx.Rules.Add(rule);
            await seedCtx.SaveChangesAsync();
            ruleId = rule.Id;
        }

        var svc = new DataFilterRuleService<TestDbContext>(factory, NullLogger<DataFilterRuleService<TestDbContext>>.Instance);

        await svc.DeleteAsync(ruleId);

        await using var ctx = factory.CreateDbContext();
        var exists = await ctx.Rules.AnyAsync(r => r.Id == ruleId);
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task SetEnabledAsync_TogglesEnabled()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        string ruleId;
        await using (var seedCtx = factory.CreateDbContext())
        {
            var rule = new TenE0DataFilterRule { EntityTypeName = "X", RuleJson = ValidRuleJson, IsEnabled = false };
            seedCtx.Rules.Add(rule);
            await seedCtx.SaveChangesAsync();
            ruleId = rule.Id;
        }

        var svc = new DataFilterRuleService<TestDbContext>(factory, NullLogger<DataFilterRuleService<TestDbContext>>.Instance);

        await svc.SetEnabledAsync(ruleId, true);

        await using var ctx = factory.CreateDbContext();
        var updated = await ctx.Rules.FindAsync(ruleId);
        updated!.IsEnabled.Should().BeTrue();
    }
}
