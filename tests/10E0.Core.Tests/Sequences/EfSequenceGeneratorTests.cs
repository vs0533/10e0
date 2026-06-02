using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TenE0.Core.Sequences;
using TenE0.Core.Sequences.Storage;

using Microsoft.Extensions.Time.Testing;

namespace TenE0.Core.Tests.Sequences;

[Trait("Category", "Unit")]
public sealed class EfSequenceGeneratorTests
{
    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TenE0Sequence> Sequences => Set<TenE0Sequence>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TenE0Sequence>(b =>
            {
                b.HasKey(e => e.Id);
                b.HasIndex(e => e.SequenceKey).IsUnique();
            });
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

    [Fact]
    public async Task NextAsync_FirstCall_CreatesRecordReturns1()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var timeProvider = new FakeTimeProvider();
        var now = DateTimeOffset.UtcNow;
        timeProvider.SetUtcNow(now);

        var gen = new EfSequenceGenerator<TestDbContext>(factory, timeProvider);

        var result = await gen.NextAsync("INV", "INV-{0000}", CancellationToken.None);

        result.Should().Match("INV-*");
        result.Should().NotBe("INV-0000");

        await using var ctx = factory.CreateDbContext();
        var seq = await ctx.Sequences.SingleAsync();
        seq.SequenceKey.Should().Be("INV");
        seq.CurrentNumber.Should().Be(1);
    }

    [Fact]
    public async Task NextAsync_SecondCall_IncrementsNumber()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var gen = new EfSequenceGenerator<TestDbContext>(factory, TimeProvider.System);

        var first = await gen.NextAsync("SEQ", "SEQ-{0000}", CancellationToken.None);
        var second = await gen.NextAsync("SEQ", "SEQ-{0000}", CancellationToken.None);

        first.Should().Be("SEQ-0001");
        second.Should().Be("SEQ-0002");

        await using var ctx = factory.CreateDbContext();
        var seq = await ctx.Sequences.SingleAsync();
        seq.CurrentNumber.Should().Be(2);
    }

    [Fact]
    public async Task NextAsync_BucketChange_ResetsTo1()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var tp = new FakeTimeProvider();
        // Day 1
        tp.SetUtcNow(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
        var gen = new EfSequenceGenerator<TestDbContext>(factory, tp);

        var first = await gen.NextAsync("ORD", "ORD-{yyyyMMdd}-{0000}", CancellationToken.None);
        first.Should().Be("ORD-20260601-0001");

        await gen.NextAsync("ORD", "ORD-{yyyyMMdd}-{0000}", CancellationToken.None); // 0002

        // Day 2 — bucket changes
        tp.SetUtcNow(new DateTimeOffset(2026, 6, 2, 10, 0, 0, TimeSpan.Zero));
        var afterBucket = await gen.NextAsync("ORD", "ORD-{yyyyMMdd}-{0000}", CancellationToken.None);

        afterBucket.Should().Be("ORD-20260602-0001");

        await using var ctx = factory.CreateDbContext();
        var seq = await ctx.Sequences.SingleAsync();
        seq.CurrentBucket.Should().Be("20260602");
        seq.CurrentNumber.Should().Be(1);
    }

    [Fact]
    public async Task NextAsync_DifferentKeys_IndependentSequences()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var factory = CreateFactory(dbName);
        var gen = new EfSequenceGenerator<TestDbContext>(factory, TimeProvider.System);

        var a1 = await gen.NextAsync("A", "A-{000}", CancellationToken.None);
        var b1 = await gen.NextAsync("B", "B-{000}", CancellationToken.None);
        var a2 = await gen.NextAsync("A", "A-{000}", CancellationToken.None);

        a1.Should().Be("A-001");
        b1.Should().Be("B-001");
        a2.Should().Be("A-002");

        await using var ctx = factory.CreateDbContext();
        var seqs = await ctx.Sequences.OrderBy(s => s.SequenceKey).ToListAsync();
        seqs.Should().HaveCount(2);
        seqs[0].SequenceKey.Should().Be("A");
        seqs[0].CurrentNumber.Should().Be(2);
        seqs[1].SequenceKey.Should().Be("B");
        seqs[1].CurrentNumber.Should().Be(1);
    }
}
