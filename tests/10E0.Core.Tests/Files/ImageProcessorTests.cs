using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TenE0.Core.Files;

namespace TenE0.Core.Tests.Files;

[Trait("Category", "Unit")]
public sealed class ImageProcessorTests
{
    private readonly ImageProcessor _sut = new();

    private static MemoryStream CreateTestImage(int width = 100, int height = 80)
    {
        using var image = new Image<Rgba32>(width, height, Color.White);
        var ms = new MemoryStream();
        image.SaveAsJpeg(ms);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task ProcessAsync_WithResize_ReturnsResizedImage()
    {
        // Arrange: 100x100 image, resize to 50x50
        using var input = CreateTestImage(100, 100);
        var options = new ImageProcessOptions { Width = 50, Height = 50 };

        // Act
        var result = await _sut.ProcessAsync(input, options);

        // Assert
        using var output = result.ProcessedStream;
        result.Success.Should().BeTrue();
        result.Width.Should().Be(50);
        result.Height.Should().Be(50);
    }

    [Fact]
    public async Task ProcessAsync_WithWatermark_ReturnsSuccess()
    {
        // Arrange
        using var input = CreateTestImage();
        var options = new ImageProcessOptions { WatermarkText = "TEST" };

        // Act
        var result = await _sut.ProcessAsync(input, options);

        // Assert
        using var output = result.ProcessedStream;
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ProcessAsync_WithQuality_ProducesNonEmptyStream()
    {
        // Arrange
        using var input = CreateTestImage();
        var options = new ImageProcessOptions { Quality = 50 };

        // Act
        var result = await _sut.ProcessAsync(input, options);

        // Assert
        using var output = result.ProcessedStream;
        result.Success.Should().BeTrue();
        output.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ProcessAsync_WithInvalidStream_ReturnsFailure()
    {
        // Arrange: random bytes that are not a valid image
        using var input = new MemoryStream(new byte[] { 0x00, 0x01, 0x02, 0x03 });

        // Act
        var result = await _sut.ProcessAsync(input, new ImageProcessOptions());

        // Assert
        using var output = result.ProcessedStream;
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ProcessAsync_WithDefaultOptions_ReturnsSuccess()
    {
        // Arrange
        using var input = CreateTestImage();
        var options = new ImageProcessOptions();

        // Act
        var result = await _sut.ProcessAsync(input, options);

        // Assert
        using var output = result.ProcessedStream;
        result.Success.Should().BeTrue();
        output.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ProcessAsync_WithWidthOnly_MaintainsAspectRatio()
    {
        // Arrange: 100x80 → Width=50 should produce 50x40 (ResizeMode.Max)
        using var input = CreateTestImage(100, 80);
        var options = new ImageProcessOptions { Width = 50 };

        // Act
        var result = await _sut.ProcessAsync(input, options);

        // Assert
        using var output = result.ProcessedStream;
        result.Success.Should().BeTrue();
        result.Width.Should().Be(50);
        result.Height.Should().Be(40);
    }

    [Fact]
    public async Task ProcessAsync_WithHeightOnly_MaintainsAspectRatio()
    {
        // Arrange: 100x80 → Height=40 should produce 50x40 (ResizeMode.Max)
        using var input = CreateTestImage(100, 80);
        var options = new ImageProcessOptions { Height = 40 };

        // Act
        var result = await _sut.ProcessAsync(input, options);

        // Assert
        using var output = result.ProcessedStream;
        result.Success.Should().BeTrue();
        result.Width.Should().Be(50);
        result.Height.Should().Be(40);
    }

    [Fact]
    public async Task GenerateThumbnailAsync_Standard_ProducesNonEmptyJpegStream()
    {
        // Arrange
        using var input = CreateTestImage();

        // Act
        using var output = await _sut.GenerateThumbnailAsync(input, 50, 50);

        // Assert
        output.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GenerateThumbnailAsync_ReturnsValidImage()
    {
        // Arrange
        using var input = CreateTestImage();

        // Act
        using var output = await _sut.GenerateThumbnailAsync(input, 50, 50);

        // Assert: output stream can be loaded by ImageSharp
        using var loaded = await Image.LoadAsync<Rgba32>(output);
        loaded.Should().NotBeNull();
    }

    [Fact]
    public async Task GenerateThumbnailAsync_WithCrop_FillsExactDimensions()
    {
        // Arrange: 100x80 → ResizeMode.Crop to 50x50 fills the box
        using var input = CreateTestImage(100, 80);

        // Act
        using var output = await _sut.GenerateThumbnailAsync(input, 50, 50);

        // Assert
        using var loaded = await Image.LoadAsync<Rgba32>(output);
        loaded.Width.Should().Be(50);
        loaded.Height.Should().Be(50);
    }

    [Fact]
    public async Task GetDimensionsAsync_KnownDimensions_ReturnsCorrectSize()
    {
        // Arrange: known 100x80 image
        using var input = CreateTestImage(100, 80);

        // Act
        var (width, height) = await _sut.GetDimensionsAsync(input);

        // Assert
        width.Should().Be(100);
        height.Should().Be(80);
    }

    [Fact]
    public async Task GetDimensionsAsync_SmallImage_ReturnsOneByOne()
    {
        // Arrange: 1x1 pixel white image
        using var input = CreateTestImage(1, 1);

        // Act
        var (width, height) = await _sut.GetDimensionsAsync(input);

        // Assert
        width.Should().Be(1);
        height.Should().Be(1);
    }
}
