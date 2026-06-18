using Microsoft.EntityFrameworkCore;
using TenE0.Core.Errors;

namespace TenE0.Core.Tests.Errors;

/// <summary>
/// Direct unit tests for <see cref="DefaultDbErrorClassifier"/>. The
/// BDD acceptance suite
/// (<see cref="DbUpdateExceptionMappingAcceptanceTests"/>) covers the
/// happy paths through the mapper; these tests pin down the classifier's
/// own contract — null guards, unrecognised inners, the
/// <see cref="DbUpdateConcurrencyException"/> short-circuit, and unknown
/// error numbers / SQLSTATEs falling through to <see cref="DbErrorKind.Other"/>.
///
/// Stand-ins mirror the real provider exception type names so the
/// dispatch is exercised end-to-end. Where the real exception types
/// would require a hard package reference (which Core deliberately
/// avoids), the stand-ins carry the dispatch key in their type name and
/// message so the provider-agnostic classifier can still inspect them.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DefaultDbErrorClassifierTests
{
    private static IDbErrorClassifier NewClassifier() => new DefaultDbErrorClassifier();

    // ── Null guard ─────────────────────────────────────────────

    [Fact]
    public void Classify_NullException_ThrowsArgumentNullException()
    {
        // Arrange
        var classifier = NewClassifier();

        // Act
        var act = () => classifier.Classify(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>(
            "the classifier's contract is explicit: a null DbUpdateException is a " +
            "system bug, not a user error, so it must fail fast at the boundary");
    }

    // ── No inner exception ─────────────────────────────────────

    [Fact]
    public void Classify_DbUpdateExceptionWithNoInner_ReturnsOther()
    {
        // Arrange
        var classifier = NewClassifier();
        var ex = new DbUpdateException("constraint violation");

        // Act
        var result = classifier.Classify(ex);

        // Assert
        result.Kind.Should().Be(DbErrorKind.Other,
            "no inner exception means the classifier has no dispatch key, " +
            "so it must fall through to Other (not throw)");
        result.EntityName.Should().BeNull();
        result.ConstraintName.Should().BeNull();
    }

    // ── Unrecognised inner type ────────────────────────────────

    [Fact]
    public void Classify_DbUpdateExceptionWrappingUnrecognisedInnerType_ReturnsOther()
    {
        // Arrange
        var classifier = NewClassifier();
        var ex = new DbUpdateException("wrap", new InvalidOperationException("not a DB error"));

        // Act
        var result = classifier.Classify(ex);

        // Assert
        result.Kind.Should().Be(DbErrorKind.Other,
            "an inner exception whose type doesn't match any known provider " +
            "(and whose message has no provider-shaped prefix) must be classified " +
            "as Other — never throw, never silently promote to a known kind");
    }

    // ── Concurrency short-circuit ──────────────────────────────

    [Fact]
    public void Classify_DbUpdateConcurrencyException_ReturnsConcurrency_EvenWithoutInner()
    {
        // Arrange — the concurrency subclass is a CLR-level concept;
        // it must be detected before the inner-inspection path.
        var classifier = NewClassifier();
        var ex = new DbUpdateConcurrencyException(
            "expected 1 row, affected 0");

        // Act
        var result = classifier.Classify(ex);

        // Assert
        result.Kind.Should().Be(DbErrorKind.Concurrency,
            "the concurrency subclass check must short-circuit BEFORE the inner " +
            "exception path so an optimistic-concurrency failure with no inner " +
            "is still classified correctly");
    }

    [Fact]
    public void Classify_DbUpdateConcurrencyException_WinsOverClassifiableInner()
    {
        // Arrange — even if the concurrency exception wraps a SQL Server
        // unique-key inner, the concurrency kind is more specific and must win.
        var classifier = NewClassifier();
        var inner = new SqlServerExceptionLike(
            number: 2627,
            message: "Violation of UNIQUE KEY constraint 'UX_Users_Email'.");
        var ex = new DbUpdateConcurrencyException("optimistic", inner);

        // Act
        var result = classifier.Classify(ex);

        // Assert
        result.Kind.Should().Be(DbErrorKind.Concurrency,
            "concurrency is a more specific signal than the underlying unique-key " +
            "violation; the client should prompt the user to reload, not show a " +
            "'duplicate value' message");
    }

    // ── SQL Server: unknown error number ───────────────────────

    [Fact]
    public void Classify_SqlServerUnknownNumber_ReturnsOther()
    {
        // Arrange
        var classifier = NewClassifier();
        var inner = new SqlServerExceptionLike(
            number: 8992,
            message: "Some unknown SQL Server error");
        var ex = new DbUpdateException("update failed", inner);

        // Act
        var result = classifier.Classify(ex);

        // Assert
        result.Kind.Should().Be(DbErrorKind.Other,
            "an SQL Server number that doesn't map to unique / FK / deadlock must " +
            "fall through to Other — the client should NOT be told 'duplicate value' " +
            "or 'referenced record not found' when neither is true");
    }

    // ── Postgres: unknown SQLSTATE ─────────────────────────────

    [Fact]
    public void Classify_PostgresUnknownSqlState_ReturnsOther()
    {
        // Arrange
        var classifier = NewClassifier();
        var inner = new PostgresExceptionLike(
            sqlState: "42P01",
            messageText: "undefined_table — a SQLSTATE that doesn't map to " +
                         "unique / FK / deadlock");
        var ex = new DbUpdateException("update failed", inner);

        // Act
        var result = classifier.Classify(ex);

        // Assert
        result.Kind.Should().Be(DbErrorKind.Other,
            "an SQLSTATE outside the known constraint families must fall through " +
            "to Other (42P01 = 'undefined_table' is a schema-level error, not a " +
            "constraint violation, and must not be misclassified as a unique-key " +
            "violation)");
    }

    // ── SQL Server: number-only-prefix on ToString (stand-in) ──
    // Pins the behavior added for unit-test stand-ins that surface the
    // 'SqlException (Number=N)' prefix only on ToString(), not on Message.

    [Fact]
    public void Classify_SqlServerStandInWithPrefixOnToString_StillClassified()
    {
        // Arrange
        var classifier = NewClassifier();
        var inner = new SqlServerExceptionToStringOnly(
            number: 2627,
            message: "Violation of UNIQUE KEY constraint 'UX_X' " +
                     "in object 'dbo.TenE0Demo'.");
        var ex = new DbUpdateException("update failed", inner);

        // Act
        var result = classifier.Classify(ex);

        // Assert
        result.Kind.Should().Be(DbErrorKind.UniqueKey,
            "the classifier's dispatch falls back to inner.ToString() when the " +
            "prefix isn't on Message — this is what makes the test stand-in " +
            "(which mirrors the real SqlException where Number is a property) " +
            "work without a hard provider reference");
        result.EntityName.Should().Be("dbo.TenE0Demo");
    }

    // ── DbErrorClassification.Unknown() is the documented fallback ─

    [Fact]
    public void Unknown_DefaultFactory_ReturnsOtherKindWithNullFields()
    {
        // Act
        var result = DbErrorClassification.Unknown();

        // Assert — the documented contract: classifier never throws, the
        // static factory is the canonical "couldn't classify" sentinel.
        result.Kind.Should().Be(DbErrorKind.Other);
        result.EntityName.Should().BeNull();
        result.ConstraintName.Should().BeNull();
    }

    // ── Entries fallback (issue #51 contract: include entity from Entries) ──
    // When the inner exception cannot be classified (unrecognised type /
    // empty message / no provider-shaped prefix), the classifier must still
    // surface the entity name from DbUpdateException.Entries — that's the
    // provider-agnostic source EF Core guarantees on every SaveChanges
    // failure. The boundary cases below lock down that contract.

    [Fact]
    public void Classify_UnrecognisedInnerWithEntries_ExposesEntityFromEntries()
    {
        // Arrange — inner type/message 不匹配任何 provider，
        // 但 Entries 有一个真实 EntityEntry（其 Metadata.ClrType
        // 为 "TenE0DemoProbe"，与 BDD 契约灯塔测试同模式 —— CLR 名是
        // EF Core 在 Entries 上稳定的来源）。classifier 必须从 Entries
        // 兜底拿到 entity 后缀。
        var entry = CreateProbeEntry();
        var ex = new DbUpdateException(
            "wrapped",
            innerException: new InvalidOperationException("not a provider error"),
            entries: new[] { entry });

        // Act
        var result = NewClassifier().Classify(ex);

        // Assert
        result.Kind.Should().Be(DbErrorKind.Other,
            "inner 不可识别 → 落到 Other，但 entity 名必须仍能透传");
        result.EntityName.Should().Be("TenE0DemoProbe",
            "issue #51 body 第 2 条：Entries 是稳定兜底，CLR 类型名必须透传");
    }

    [Fact]
    public void Classify_UnrecognisedInnerWithoutEntries_ReturnsUnknownFallback()
    {
        // Arrange — 无 inner、也无 entries。这是双重最坏路径，classifier
        // 必须不抛、EntityName 留 null，让 mapper 走纯 fallback message。
        var ex = new DbUpdateException("wrapped");

        // Act
        var result = NewClassifier().Classify(ex);

        // Assert — 与既有 Classify_DbUpdateExceptionWithNoInner_ReturnsOther
        // 一致；本次新增断言 entries 兜底未污染 null 路径。
        result.Kind.Should().Be(DbErrorKind.Other);
        result.EntityName.Should().BeNull();
    }

    [Fact]
    public void Classify_ClassifiableInner_EntityFromMessageWinsOverEntries()
    {
        // Arrange — inner 是 SQL Server 2627（message 里能解析出
        // "dbo.TenE0Role"），但 Entries 是 "TenE0DemoProbe"。两个来源都能
        // 拿 entity，message 路径更精准（带 schema 前缀 + 与业务表
        // 一致），必须优先；Entries 仅作 message-parse 失败的兜底。
        var entry = CreateProbeEntry();
        var inner = new SqlServerExceptionLike(
            number: 2627,
            message: "Violation of UNIQUE KEY constraint 'UX_TenE0Role_Code'. " +
                     "Cannot insert duplicate key in object 'dbo.TenE0Role'.");
        var ex = new DbUpdateException(
            "wrapped",
            innerException: inner,
            entries: new[] { entry });

        // Act
        var result = NewClassifier().Classify(ex);

        // Assert
        result.Kind.Should().Be(DbErrorKind.UniqueKey);
        result.EntityName.Should().Be("dbo.TenE0Role",
            "message 解析成功时，EntityName 走 message 路径而非 Entries 兜底");
    }

    // ── Probe DbContext（真实构造 EntityEntry，与 BDD 文件同模式） ────

    private sealed class TenE0DemoProbe
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class ProbeDbContext : DbContext
    {
        public ProbeDbContext(DbContextOptions<ProbeDbContext> options) : base(options) { }
        public DbSet<TenE0DemoProbe> Demos => Set<TenE0DemoProbe>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TenE0DemoProbe>(b =>
            {
                b.HasKey(x => x.Id);
                b.Property(x => x.Name).HasMaxLength(64);
                b.ToTable("TenE0Demo");
            });
        }
    }

    private static Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry CreateProbeEntry()
    {
        var options = new DbContextOptionsBuilder<ProbeDbContext>()
            .UseInMemoryDatabase($"classifier-probe-{Guid.NewGuid():N}")
            .Options;
        using var ctx = new ProbeDbContext(options);
        var entity = new TenE0DemoProbe { Id = 1, Name = "x" };
        ctx.Demos.Add(entity);
        return ctx.Entry(entity);
    }

    // ── Stand-ins (mirror the BDD acceptance tests) ─────────────

    private sealed class SqlServerExceptionLike : Exception
    {
        public int Number { get; }
        public SqlServerExceptionLike(int number, string message) : base(message)
        {
            Number = number;
        }
        public override string ToString() => $"SqlException (Number={Number}): {Message}";
    }

    /// <summary>
    /// Variant that keeps the 'SqlException (Number=N)' prefix only on
    /// ToString() — mirrors the real <c>Microsoft.Data.SqlClient.SqlException</c>
    /// whose Number is a property, not a message field. The classifier must
    /// fall back to ToString() to detect this shape.
    /// </summary>
    private sealed class SqlServerExceptionToStringOnly : Exception
    {
        public int Number { get; }
        public SqlServerExceptionToStringOnly(int number, string message) : base(message)
        {
            Number = number;
        }
        public override string ToString() => $"SqlException (Number={Number}): {Message}";
    }

    private sealed class PostgresExceptionLike : Exception
    {
        public string SqlState { get; }
        public PostgresExceptionLike(string sqlState, string messageText)
            : base($"{sqlState}: {messageText}")
        {
            SqlState = sqlState;
        }
    }
}
