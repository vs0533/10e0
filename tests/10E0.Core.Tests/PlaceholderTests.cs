namespace TenE0.Core.Tests;

[Trait("Category", "Smoke")]
public class PlaceholderTests
{
    [Fact]
    public void FrameworkCore_ShouldBeReferencable()
    {
        // Arrange & Act: verify the Core assembly loads
        var assembly = typeof(TenE0.Core.Abstractions.ICommand<>).Assembly;

        // Assert
        assembly.Should().NotBeNull();
        assembly.GetName().Name.Should().Be("10E0.Core");
    }
}
