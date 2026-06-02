using TenE0.Core.Sequences;

namespace TenE0.Core.Tests.Sequences;

public sealed class SequenceFormatTests
{
    #region Parse

    [Fact]
    public void Parse_Valid_ShouldExtractSegments()
    {
        // Act
        var parsed = SequenceFormat.Parse("ORD-{yyyyMMdd}-{0000}");

        // Assert
        parsed.Segments.Should().HaveCount(4);
        parsed.Segments[0].Should().BeOfType<SequenceFormat.Segment.Literal>();
        ((SequenceFormat.Segment.Literal)parsed.Segments[0]).Text.Should().Be("ORD-");
        parsed.Segments[1].Should().BeOfType<SequenceFormat.Segment.DatePlaceholder>();
        ((SequenceFormat.Segment.DatePlaceholder)parsed.Segments[1]).Format.Should().Be("yyyyMMdd");
        parsed.Segments[2].Should().BeOfType<SequenceFormat.Segment.Literal>();
        ((SequenceFormat.Segment.Literal)parsed.Segments[2]).Text.Should().Be("-");
        parsed.Segments[3].Should().BeOfType<SequenceFormat.Segment.SequencePlaceholder>();
        ((SequenceFormat.Segment.SequencePlaceholder)parsed.Segments[3]).Width.Should().Be(4);

        parsed.DateToken.Should().Be("yyyyMMdd");
        parsed.SequenceWidth.Should().Be(4);
    }

    [Fact]
    public void Parse_NoDate_ShouldWork()
    {
        // Act
        var parsed = SequenceFormat.Parse("INV-{0000}");

        // Assert
        parsed.DateToken.Should().BeNull();
        parsed.SequenceWidth.Should().Be(4);
        parsed.Segments.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_MultipleSequences_ShouldThrow()
    {
        var act = () => SequenceFormat.Parse("{0000}-{0000}");
        act.Should().Throw<ArgumentException>().WithMessage("*只能含一个序号占位*");
    }

    [Fact]
    public void Parse_MultipleDates_ShouldThrow()
    {
        var act = () => SequenceFormat.Parse("{yyyy}-{MM}-{0000}");
        act.Should().Throw<ArgumentException>().WithMessage("*只能含一个日期占位*");
    }

    [Fact]
    public void Parse_NoSequence_ShouldThrow()
    {
        var act = () => SequenceFormat.Parse("ORD-{yyyyMMdd}");
        act.Should().Throw<ArgumentException>().WithMessage("*必须含一个序号占位*");
    }

    [Fact]
    public void Parse_Empty_ShouldThrow()
    {
        var act = () => SequenceFormat.Parse("");
        act.Should().Throw<ArgumentException>().WithMessage("*不能为空*");
    }

    [Fact]
    public void Parse_Null_ShouldThrow()
    {
        var act = () => SequenceFormat.Parse(null!);
        act.Should().Throw<ArgumentException>().WithMessage("*不能为空*");
    }

    #endregion

    #region RenderBucket

    [Fact]
    public void RenderBucket_WithDate_ShouldFormatCorrectly()
    {
        // Arrange
        var parsed = SequenceFormat.Parse("ORD-{yyyyMMdd}-{0000}");
        var now = new DateTimeOffset(2026, 6, 1, 10, 30, 0, TimeSpan.Zero);

        // Act
        var bucket = SequenceFormat.RenderBucket(parsed, now);

        // Assert
        bucket.Should().Be("20260601");
    }

    [Fact]
    public void RenderBucket_WithoutDate_ShouldReturnUnderscore()
    {
        // Arrange
        var parsed = SequenceFormat.Parse("INV-{0000}");

        // Act
        var bucket = SequenceFormat.RenderBucket(parsed, DateTimeOffset.UtcNow);

        // Assert
        bucket.Should().Be("_");
    }

    #endregion

    #region Render

    [Fact]
    public void Render_WithAllParts_ShouldProduceCorrectString()
    {
        // Arrange
        var parsed = SequenceFormat.Parse("ORD-{yyyyMMdd}-{0000}");
        var now = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        // Act
        var result = SequenceFormat.Render(parsed, 42, now);

        // Assert
        result.Should().Be("ORD-20260601-0042");
    }

    [Fact]
    public void Render_SequencePadding_ShouldPadWithZeros()
    {
        // Arrange
        var parsed = SequenceFormat.Parse("NO-{yyyyMM}-{00000}");
        var now = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);

        // Act
        var result = SequenceFormat.Render(parsed, 1, now);

        // Assert
        result.Should().Be("NO-202601-00001");
    }

    #endregion

    #region Complex format
    #endregion
}
