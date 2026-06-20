using TenE0.Core.Files.Storage;
using Microsoft.Extensions.Options;

namespace TenE0.Core.Tests.Files.Storage;

[Trait("Category", "Unit")]
public sealed class LocalFileStorageTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LocalFileStorage _sut;

    public LocalFileStorageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"10e0-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        var options = Options.Create(new LocalStorageOptions { BasePath = _tempDir, BaseUrl = "http://localhost/uploads" });
        _sut = new LocalFileStorage(TimeProvider.System, options);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task StoreAsync_ValidStream_StoresFileOnDisk()
    {
        // Arrange
        using var stream = new MemoryStream();
        var data = "hello world"u8.ToArray();
        await stream.WriteAsync(data);
        stream.Position = 0;

        // Act
        var result = await _sut.StoreAsync(stream, "test.txt", "text/plain");

        // Assert
        result.Success.Should().BeTrue();
        result.StoragePath.Should().NotBeNullOrEmpty();
        result.AccessUrl.Should().Contain(".txt");
        result.AccessUrl.Should().StartWith("http://localhost/uploads/");

        var fullPath = Path.Combine(_tempDir, result.StoragePath);
        File.Exists(fullPath).Should().BeTrue();
        var fileContent = await File.ReadAllBytesAsync(fullPath);
        fileContent.Should().Equal(data);
    }

    [Fact]
    public async Task StoreAsync_SubdirectoryPath_CreatesSubdirectories()
    {
        // Arrange
        using var stream = new MemoryStream();
        var data = "subdir test"u8.ToArray();
        await stream.WriteAsync(data);
        stream.Position = 0;

        // Act
        var result = await _sut.StoreAsync(stream, "subdir/test.txt", "text/plain");

        // Assert
        result.Success.Should().BeTrue();

        var fullPath = Path.Combine(_tempDir, result.StoragePath);
        File.Exists(fullPath).Should().BeTrue();
    }

    [Fact]
    public async Task RetrieveAsync_ExistingFile_ReturnsStreamWithContent()
    {
        // Arrange
        using var sourceStream = new MemoryStream();
        var data = "hello world"u8.ToArray();
        await sourceStream.WriteAsync(data);
        sourceStream.Position = 0;

        var storeResult = await _sut.StoreAsync(sourceStream, "test.txt", "text/plain");

        // Act
        var retrieved = await _sut.RetrieveAsync(storeResult.StoragePath);

        // Assert
        retrieved.Should().NotBeNull();
        using var ms = new MemoryStream();
        await retrieved!.CopyToAsync(ms);
        ms.ToArray().Should().Equal(data);
    }

    [Fact]
    public async Task RetrieveAsync_MissingFile_ReturnsNull()
    {
        // Act
        var result = await _sut.RetrieveAsync("nonexistent.txt");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ExistingFile_ReturnsTrueAndRemovesFile()
    {
        // Arrange
        using var stream = new MemoryStream();
        var data = "delete me"u8.ToArray();
        await stream.WriteAsync(data);
        stream.Position = 0;

        var storeResult = await _sut.StoreAsync(stream, "test.txt", "text/plain");
        var fullPath = Path.Combine(_tempDir, storeResult.StoragePath);

        // Act
        var deleted = await _sut.DeleteAsync(storeResult.StoragePath);

        // Assert
        deleted.Should().BeTrue();
        File.Exists(fullPath).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_MissingFile_ReturnsFalse()
    {
        // Act
        var result = await _sut.DeleteAsync("nonexistent.txt");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void GetAccessUrl_UnixPath_CombinesBaseUrlAndPath()
    {
        // Act
        var url = _sut.GetAccessUrl("2026/06/test.txt");

        // Assert
        url.Should().Be("http://localhost/uploads/2026/06/test.txt");
    }

    [Fact]
    public void GetAccessUrl_WindowsBackslash_ConvertsToForwardSlash()
    {
        // Act
        var url = _sut.GetAccessUrl("2026\\06\\test.txt");

        // Assert
        url.Should().Be("http://localhost/uploads/2026/06/test.txt");
    }

    [Fact]
    public async Task ExistsAsync_ExistingFile_ReturnsTrue()
    {
        // Arrange
        using var stream = new MemoryStream();
        var data = "exists"u8.ToArray();
        await stream.WriteAsync(data);
        stream.Position = 0;

        var storeResult = await _sut.StoreAsync(stream, "test.txt", "text/plain");

        // Act
        var exists = await _sut.ExistsAsync(storeResult.StoragePath);

        // Assert
        exists.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_MissingFile_ReturnsFalse()
    {
        // Act
        var exists = await _sut.ExistsAsync("nonexistent.txt");

        // Assert
        exists.Should().BeFalse();
    }
}
