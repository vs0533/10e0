using TenE0.Core.Queries;

namespace TenE0.Core.Tests.Queries;

public sealed class PagedQueryTests
{
    #region PagedQuery record

    [Fact]
    public void PagedQuery_Defaults_ShouldBeCorrect()
    {
        // Act
        var query = new PagedQuery();

        // Assert
        query.Page.Should().Be(1);
        query.PageSize.Should().Be(20);
        query.OrderBy.Should().BeNull();
        query.Where.Should().BeNull();
    }

    [Fact]
    public void PagedQuery_WithAllFields_ShouldSetValues()
    {
        // Act
        var query = new PagedQuery(Page: 3, PageSize: 50, OrderBy: "Name desc", Where: "Status == 'Active'");

        // Assert
        query.Page.Should().Be(3);
        query.PageSize.Should().Be(50);
        query.OrderBy.Should().Be("Name desc");
        query.Where.Should().Be("Status == 'Active'");
    }

    #endregion

    #region PagedResult<T>.Create

    [Fact]
    public void Create_ExactDivision_TotalPagesCorrect()
    {
        // Arrange
        var items = new List<string> { "a", "b", "c", "d" };

        // Act
        var result = PagedResult<string>.Create(items, 20, 1, 5);

        // Assert
        result.TotalPages.Should().Be(4, "20 / 5 = 4 exact pages");
    }

    [Fact]
    public void Create_RoundUp_TotalPagesCorrect()
    {
        // Arrange
        var items = new List<string> { "a", "b" };

        // Act
        var result = PagedResult<string>.Create(items, 22, 1, 5);

        // Assert
        result.TotalPages.Should().Be(5, "22 / 5 = ceil(4.4) = 5");
    }

    [Fact]
    public void Create_ZeroTotal_TotalPagesZero()
    {
        // Arrange
        var items = Array.Empty<string>();

        // Act
        var result = PagedResult<string>.Create(items, 0, 1, 10);

        // Assert
        result.TotalPages.Should().Be(0);
    }

    [Fact]
    public void Create_AllFields_Preserved()
    {
        // Arrange
        var items = new List<int> { 42 };

        // Act
        var result = PagedResult<int>.Create(items, 100, 2, 10);

        // Assert
        result.Items.Should().Equal(42);
        result.Total.Should().Be(100);
        result.Page.Should().Be(2);
        result.PageSize.Should().Be(10);
        result.TotalPages.Should().Be(10);
    }

    #endregion
}
