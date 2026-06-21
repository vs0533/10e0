using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.DependencyInjection;
using TenE0.Core.Files;
using TenE0.Core.Files.Storage;

namespace TenE0.Core.Tests.Files;

[Trait("Category", "Unit")]
public sealed class FilesExtensionsTests
{
    /// <summary>
    /// 占位 DbContext — AddTenE0Files 本身只注册 FileService 和存储后端，不要求调用方传具体 DbContext。
    /// 实际 DbContext 仍需在 AddTenE0Files&lt;TContext&gt; 的泛型实参处指定；本测试只需验证服务描述符被注册。
    /// </summary>
    private sealed class NoopDbContext(DbContextOptions<NoopDbContext> options) : DbContext(options)
    {
    }

    [Fact]
    public void AddTenE0Files_RegistersAllExpectedServices()
    {
        var services = new ServiceCollection();

        services.AddTenE0Files<NoopDbContext>();

        services.Should().Contain(sd => sd.ServiceType == typeof(IFileStorage) && sd.ImplementationType == typeof(LocalFileStorage));
        services.Should().Contain(sd => sd.ServiceType == typeof(IImageProcessor) && sd.ImplementationType == typeof(ImageProcessor));
        services.Should().Contain(sd => sd.ServiceType == typeof(IFileService));
    }

    [Fact]
    public void AddTenE0FilesWithAwsS3_RegistersAwsS3Storage()
    {
        var services = new ServiceCollection();

        services.AddTenE0FilesWithAwsS3<NoopDbContext>(opts => opts.BucketName = "test");

        services.Should().Contain(sd => sd.ServiceType == typeof(IFileStorage) && sd.ImplementationType == typeof(AwsS3Storage));
    }
}
