using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TenE0.Core.Organizations;

namespace TenE0.Core.Tests.Organizations;

[Trait("Category", "Unit")]
public sealed class OrgTreeServiceTests
{
    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TenE0Org> Orgs => Set<TenE0Org>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            optionsBuilder.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TenE0Org>(b =>
            {
                b.HasKey(e => e.Id);
                b.Property(e => e.Path);
                b.Property(e => e.Level);
                b.Property(e => e.Code);
            });
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

    private OrgTreeService<TestDbContext> CreateService(string dbName)
        => new(CreateFactory(dbName));

    // ================================================================
    // AddAsync Tests
    // ================================================================

    [Fact]
    public async Task AddAsync_RootNode_SetsPathStartingWithSlashAndLevel0()
    {
        var svc = CreateService(Guid.NewGuid().ToString("N"));

        var node = await svc.AddAsync("dept-01", "技术部");

        node.Level.Should().Be(0);
        node.Path.Should().StartWith("/").And.EndWith("/");
        node.Path.Should().Contain(node.Id);
        node.ParentId.Should().BeNull();
    }

    [Fact]
    public async Task AddAsync_ChildNode_InheritsParentPathAndLevelPlus1()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var svc = CreateService(dbName);
        var parent = await svc.AddAsync("p", "父部门");

        var child = await svc.AddAsync("c", "子部门", parentId: parent.Id);

        child.Level.Should().Be(1);
        child.Path.Should().StartWith(parent.Path);
        child.Path.Should().Contain(child.Id);
        child.ParentId.Should().Be(parent.Id);
    }

    [Fact]
    public async Task AddAsync_DeepNesting_BuildsCorrectLevel3Path()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var svc = CreateService(dbName);
        var root = await svc.AddAsync("r", "根");
        var l1 = await svc.AddAsync("l1", "一层", parentId: root.Id);
        var l2 = await svc.AddAsync("l2", "二层", parentId: l1.Id);
        var l3 = await svc.AddAsync("l3", "三层", parentId: l2.Id);

        l3.Level.Should().Be(3);
        l3.Path.Should().StartWith(root.Path);
        l3.Path.Split('/').Length.Should().BeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public async Task AddAsync_InvalidParentId_ThrowsInvalidOperation()
    {
        var svc = CreateService(Guid.NewGuid().ToString("N"));

        var act = () => svc.AddAsync("x", "节点", parentId: "nonexistent");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*父节点不存在*");
    }

    [Fact]
    public async Task AddAsync_DescriptionAndOrder_AreStored()
    {
        var svc = CreateService(Guid.NewGuid().ToString("N"));

        var node = await svc.AddAsync("dept", "部门", description: "研发中心", order: 5);

        node.Description.Should().Be("研发中心");
        node.Order.Should().Be(5);
    }

    // ================================================================
    // MoveAsync Tests
    // ================================================================

    [Fact]
    public async Task MoveAsync_ToRoot_UpdatesPathAndLevel()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var svc = CreateService(dbName);
        var root = await svc.AddAsync("r", "根");
        var child = await svc.AddAsync("c", "子", parentId: root.Id);
        child.Level.Should().Be(1);

        await svc.MoveAsync(child.Id, null);

        var factory = CreateFactory(dbName);
        await using var ctx = factory.CreateDbContext();
        var moved = await ctx.Orgs.FindAsync(child.Id);
        moved!.Level.Should().Be(0);
        moved.Path.Should().NotContain(root.Id);
        moved.ParentId.Should().BeNull();
    }

    [Fact]
    public async Task MoveAsync_ToSibling_UpdatesPathPrefix()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var svc = CreateService(dbName);
        var root = await svc.AddAsync("r", "根");
        var branchA = await svc.AddAsync("a", "A分支", parentId: root.Id);
        var branchB = await svc.AddAsync("b", "B分支", parentId: root.Id);
        var leaf = await svc.AddAsync("leaf", "叶子", parentId: branchA.Id);

        await svc.MoveAsync(leaf.Id, branchB.Id);

        var factory = CreateFactory(dbName);
        await using var ctx = factory.CreateDbContext();
        var moved = await ctx.Orgs.FindAsync(leaf.Id);
        moved!.Path.Should().StartWith(branchB.Path);
        moved.Path.Should().NotContain(branchA.Id);
    }

    [Fact]
    public async Task MoveAsync_ToSelf_Throws()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var svc = CreateService(dbName);
        var node = await svc.AddAsync("n", "节点");

        var act = () => svc.MoveAsync(node.Id, node.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*自身*");
    }

    [Fact]
    public async Task MoveAsync_ToDescendant_Throws()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var svc = CreateService(dbName);
        var parent = await svc.AddAsync("p", "父");
        var child = await svc.AddAsync("c", "子", parentId: parent.Id);

        var act = () => svc.MoveAsync(parent.Id, child.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*后代*");
    }

    [Fact]
    public async Task MoveAsync_UpdatesAllDescendantsPaths()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var svc = CreateService(dbName);
        var rootA = await svc.AddAsync("a", "A");
        var rootB = await svc.AddAsync("b", "B");
        var sub = await svc.AddAsync("sub", "Sub", parentId: rootA.Id);
        var grandChild = await svc.AddAsync("gc", "GC", parentId: sub.Id);

        await svc.MoveAsync(sub.Id, rootB.Id);

        var factory = CreateFactory(dbName);
        await using var ctx = factory.CreateDbContext();
        var movedSub = await ctx.Orgs.FindAsync(sub.Id);
        var movedGc = await ctx.Orgs.FindAsync(grandChild.Id);

        movedSub!.Path.Should().StartWith(rootB.Path);
        movedSub.Level.Should().Be(1);
        movedGc!.Path.Should().StartWith(movedSub.Path);
    }

    // ================================================================
    // GetDescendantsAsync Tests
    // ================================================================

    [Fact]
    public async Task GetDescendantsAsync_ReturnsDirectChildrenExcludingSelf()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var svc = CreateService(dbName);
        var root = await svc.AddAsync("r", "根");
        await svc.AddAsync("c1", "子1", parentId: root.Id);
        await svc.AddAsync("c2", "子2", parentId: root.Id);

        var descendants = await svc.GetDescendantsAsync(root.Id);

        descendants.Should().HaveCount(2);
        descendants.Should().NotContain(d => d.Id == root.Id);
    }

    [Fact]
    public async Task GetDescendantsAsync_NonExistentNode_ReturnsEmpty()
    {
        var svc = CreateService(Guid.NewGuid().ToString("N"));

        var result = await svc.GetDescendantsAsync("nonexistent");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDescendantsAsync_ReturnsDescendantsInLevelThenOrder()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var svc = CreateService(dbName);
        var root = await svc.AddAsync("r", "根");
        var c2 = await svc.AddAsync("c2", "C2", parentId: root.Id, order: 2);
        var c1 = await svc.AddAsync("c1", "C1", parentId: root.Id, order: 1);
        await svc.AddAsync("gc", "GC", parentId: c1.Id, order: 0);

        var descendants = await svc.GetDescendantsAsync(root.Id);

        descendants[0].Level.Should().Be(1);
        descendants[1].Level.Should().Be(1);
    }

    // ================================================================
    // GetAncestorsAsync Tests
    // ================================================================

    [Fact]
    public async Task GetAncestorsAsync_ReturnsAncestorsClosestFirst()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var svc = CreateService(dbName);
        var root = await svc.AddAsync("r", "根");
        var l1 = await svc.AddAsync("l1", "L1", parentId: root.Id);
        var l2 = await svc.AddAsync("l2", "L2", parentId: l1.Id);

        var ancestors = await svc.GetAncestorsAsync(l2.Id);

        ancestors.Should().HaveCount(2);
        ancestors[0].Id.Should().Be(l1.Id);
        ancestors[1].Id.Should().Be(root.Id);
    }

    [Fact]
    public async Task GetAncestorsAsync_RootNode_ReturnsEmpty()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var svc = CreateService(dbName);
        var root = await svc.AddAsync("r", "根");

        var ancestors = await svc.GetAncestorsAsync(root.Id);

        ancestors.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAncestorsAsync_NonExistentNode_ReturnsEmpty()
    {
        var svc = CreateService(Guid.NewGuid().ToString("N"));

        var result = await svc.GetAncestorsAsync("nonexistent");

        result.Should().BeEmpty();
    }

    // ================================================================
    // GetSubtreeIdsAsync Tests
    // ================================================================

    [Fact]
    public async Task GetSubtreeIdsAsync_IncludesSelfAndDescendants()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var svc = CreateService(dbName);
        var root = await svc.AddAsync("r", "根");
        var child = await svc.AddAsync("c", "子", parentId: root.Id);

        var ids = await svc.GetSubtreeIdsAsync(root.Id);

        ids.Should().Contain(root.Id);
        ids.Should().Contain(child.Id);
        ids.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetSubtreeIdsAsync_NonExistentNode_ReturnsEmptySet()
    {
        var svc = CreateService(Guid.NewGuid().ToString("N"));

        var result = await svc.GetSubtreeIdsAsync("nonexistent");

        result.Should().BeEmpty();
    }
}
