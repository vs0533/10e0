namespace TenE0.Api.Tests;

public class PlaceholderTests
{
    [Fact]
    public void ApiProject_ShouldBeReferencable()
    {
        // Arrange & Act: verify the Api assembly loads
        var assembly = typeof(Program).Assembly;

        // Assert
        Assert.NotNull(assembly);
        Assert.Equal("10E0.Api", assembly.GetName().Name);
    }
}
