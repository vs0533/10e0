using TenE0.Core.Scheduling;

namespace TenE0.Core.Tests.Scheduling;

[Trait("Category", "Unit")]
public sealed class CronExtensionsTests
{
    // ================================================================
    // Parse / IsValid
    // ================================================================

    [Theory]
    [InlineData("0 0 9 * * ?")]      // 每天 9:00（6 字段含秒）
    [InlineData("0 0 0 1 * ?")]      // 每月 1 号 0 点
    [InlineData("0 0 2 ? * MON")]    // 每周一 2:00
    [InlineData("0 */5 * * * ?")]    // 每 5 分钟
    public void IsValid_LegalExpression_ReturnsTrue(string cron)
        => CronExtensions.IsValid(cron).Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("not a cron")]
    [InlineData("99 99 99 * * ?")]   // 字段超界
    [InlineData("* * * *")]          // 字段不足
    public void IsValid_IllegalExpression_ReturnsFalse(string cron)
        => CronExtensions.IsValid(cron).Should().BeFalse();

    [Fact]
    public void Parse_IllegalExpression_ThrowsArgumentException()
    {
        var act = () => CronExtensions.Parse("garbage", "my-job");
        act.Should().Throw<ArgumentException>()
            .WithMessage("*my-job*");
    }

    [Fact]
    public void Parse_NullOrWhiteSpace_Throws()
    {
        var act1 = () => CronExtensions.Parse("");
        var act2 = () => CronExtensions.Parse("   ");
        act1.Should().Throw<ArgumentException>();
        act2.Should().Throw<ArgumentException>();
    }

    // ================================================================
    // GetNextOccurrence
    // ================================================================

    [Fact]
    public void GetNextOccurrence_DailyAt9_ReturnsNext9AM()
    {
        // 2024-06-15 08:00 UTC → 下次 2024-06-15 09:00 UTC
        var from = new DateTimeOffset(2024, 6, 15, 8, 0, 0, TimeSpan.Zero);
        var next = CronExtensions.GetNextOccurrence("0 0 9 * * ?", from);
        next.Should().Be(new DateTimeOffset(2024, 6, 15, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void GetNextOccurrence_After9AM_RollsToNextDay()
    {
        // 2024-06-15 10:00 UTC → 下次 2024-06-16 09:00 UTC
        var from = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
        var next = CronExtensions.GetNextOccurrence("0 0 9 * * ?", from);
        next.Should().Be(new DateTimeOffset(2024, 6, 16, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void GetNextOccurrence_FirstOfMonth_ReturnsNextMonthFirst()
    {
        // 2024-06-15 12:00 UTC → 下次 2024-07-01 00:00 UTC
        var from = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var next = CronExtensions.GetNextOccurrence("0 0 0 1 * ?", from);
        next.Should().Be(new DateTimeOffset(2024, 7, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void GetNextOccurrence_WeeklyMonday_ReturnsNextMonday()
    {
        // 2024-06-15 是周六，15:00 UTC → 下次周一 2024-06-17 02:00 UTC
        var from = new DateTimeOffset(2024, 6, 15, 15, 0, 0, TimeSpan.Zero);
        var next = CronExtensions.GetNextOccurrence("0 0 2 ? * MON", from);
        next.Should().Be(new DateTimeOffset(2024, 6, 17, 2, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void GetNextOccurrence_EveryMinute_SoonNextMinute()
    {
        var from = new DateTimeOffset(2024, 6, 15, 8, 30, 20, TimeSpan.Zero);
        var next = CronExtensions.GetNextOccurrence("0 * * * * ?", from);
        next.Should().Be(new DateTimeOffset(2024, 6, 15, 8, 31, 0, TimeSpan.Zero));
    }

    [Fact]
    public void GetNextOccurrence_RespectsTimeZone()
    {
        // Cron 用 +08:00 时区：2024-06-15 00:30 UTC = 08:30 +08:00
        // "0 0 9 * * ?" 在 +08:00 的 9:00 = UTC 01:00
        var from = new DateTimeOffset(2024, 6, 15, 0, 30, 0, TimeSpan.Zero);
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        var next = CronExtensions.GetNextOccurrence("0 0 9 * * ?", from, tz);
        // UTC 01:00 = +08:00 09:00
        next.Should().Be(new DateTimeOffset(2024, 6, 15, 1, 0, 0, TimeSpan.Zero));
    }
}
