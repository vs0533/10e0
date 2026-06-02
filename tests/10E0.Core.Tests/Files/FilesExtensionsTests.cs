using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.DependencyInjection;
using TenE0.Core.Files;
using TenE0.Core.Files.Storage;

namespace TenE0.Core.Tests.Files;

[Trait("Category", "Unit")]
public sealed class FilesExtensionsTests
{
    [Fact]
    public void AddTenE0Files_RegistersAllExpectedServices()
    {
        var services = new ServiceCollection();

        services.AddTenE0Files();

        services.Should().Contain(sd => sd.ServiceType == typeof(IFileStorage) && sd.ImplementationType == typeof(LocalFileStorage));
        services.Should().Contain(sd => sd.ServiceType == typeof(IImageProcessor) && sd.ImplementationType == typeof(ImageProcessor));
        services.Should().Contain(sd => sd.ServiceType == typeof(IFileService));
    }

    [Fact]
    public void AddTenE0FilesWithAwsS3_RegistersAwsS3Storage()
    {
        var services = new ServiceCollection();

        services.AddTenE0FilesWithAwsS3(opts => opts.BucketName = "test");

        services.Should().Contain(sd => sd.ServiceType == typeof(IFileStorage) && sd.ImplementationType == typeof(AwsS3Storage));
    }
}
