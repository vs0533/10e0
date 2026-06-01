namespace TenE0.Core.Tests;

public class PlaceholderTests
{
    [Fact]
    public void FrameworkCore_ShouldBeReferencable()
    {
        // Arrange & Act: verify the Core assembly loads
        var assembly = typeof(TenE0.Core.Abstractions.ICommand<>).Assembly;

        // Assert
        Assert.NotNull(assembly);
        Assert.Equal("10E0.Core", assembly.GetName().Name);
    }
}
