using TenE0.Core.Queries;

namespace TenE0.Core.Tests.Queries;

public sealed class DynamicQueryExtensionsTests
{
    private static IQueryable<int> Range(int count = 10) =>
        Enumerable.Range(1, count).AsQueryable();

    #region DynamicWhere

    [Fact]
    public void DynamicWhere_NullPredicate_ShouldReturnSource()
    {
        var source = Range(10);
        var result = source.DynamicWhere(null!);
        result.Should().BeEquivalentTo(source);
    }

    [Fact]
    public void DynamicWhere_EmptyPredicate_ShouldReturnSource()
    {
        var source = Range(10);
        var result = source.DynamicWhere("");
        result.Should().BeEquivalentTo(source);
    }

    [Fact]
    public void DynamicWhere_ValidPredicate_ShouldFilter()
    {
        var source = Range(10);
        var result = source.DynamicWhere("it > @0", 5);
        result.Should().BeEquivalentTo(new[] { 6, 7, 8, 9, 10 });
    }

    #endregion

    #region DynamicOrderBy

    [Fact]
    public void DynamicOrderBy_Null_ShouldReturnSource()
    {
        var source = Range(10);
        var result = source.DynamicOrderBy(null!);
        result.Should().BeEquivalentTo(source);
    }

    [Fact]
    public void DynamicOrderBy_Valid_ShouldOrder()
    {
        var source = new int[] { 3, 1, 4, 1, 5 }.AsQueryable();
        var result = source.DynamicOrderBy("it asc");
        result.Should().BeEquivalentTo(new[] { 1, 1, 3, 4, 5 });
    }

    #endregion

    #region DynamicSelect

    [Fact]
    public void DynamicSelect_Null_ShouldReturnSource()
    {
        var source = Range(3);
        var result = source.DynamicSelect(null!);
        result.Should().BeEquivalentTo(source);
    }

    #endregion

    #region Page

    [Fact]
    public void Page_Parameters_NormalizeInvalidPage()
    {
        var source = Range(10);
        var result = source.Page(0, 10).ToList();
        result.Should().HaveCount(10);
        result.Should().StartWith(1);
    }

    [Fact]
    public void Page_Parameters_NormalizeInvalidPageSize()
    {
        var source = Range(20);
        var result = source.Page(1, 0).ToList();
        result.Should().HaveCount(10, "pageSize=0 should normalize to 10");
    }

    [Fact]
    public void Page_Parameters_CapPageSize()
    {
        var source = Range(2000);
        var result = source.Page(1, 9999).ToList();
        result.Should().HaveCount(1000, "pageSize should be capped to 1000");
    }

    [Fact]
    public void Page_Parameters_CorrectSkipAndTake()
    {
        var source = Range(100);
        var result = source.Page(3, 10).ToList();
        result.Should().BeEquivalentTo(Enumerable.Range(21, 10));
    }

    #endregion

    #region WhereIf

    [Fact]
    public void WhereIf_ConditionTrue_ShouldApply()
    {
        var source = Range(10);
        var result = source.WhereIf(true, "it > @0", 5);
        result.Should().BeEquivalentTo(new[] { 6, 7, 8, 9, 10 });
    }

    [Fact]
    public void WhereIf_ConditionFalse_ShouldSkip()
    {
        var source = Range(10);
        var result = source.WhereIf(false, "it > @0", 5);
        result.Should().BeEquivalentTo(source);
    }

    #endregion
}
