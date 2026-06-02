namespace TenE0.Api.Tests;

[Trait("Category", "Smoke")]
public class PlaceholderTests
{
    [Fact]
    public void ApiProject_ShouldBeReferencable()
    {
        // Arrange & Act: verify the Api assembly loads
        var assembly = typeof(Program).Assembly;

        // Assert
        assembly.Should().NotBeNull();
        assembly.GetName().Name.Should().Be("10E0.Api");
    }
}
