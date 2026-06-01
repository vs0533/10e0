using Microsoft.Extensions.DependencyInjection;
using TenE0.Core.Files;
using TenE0.Core.Files.Storage;

namespace TenE0.Core.DependencyInjection;

/// <summary>
/// 文件上传功能 DI 注册扩展
/// </summary>
public static class FilesExtensions
{
    /// <summary>
    /// 添加文件上传功能（使用本地存储）
    /// </summary>
    public static IServiceCollection AddTenE0Files(this IServiceCollection services, Action<LocalStorageOptions>? configure = null)
    {
        var options = new LocalStorageOptions();
        configure?.Invoke(options);

        services.Configure(configure ?? (opts => { }));
        services.AddScoped<IFileStorage, LocalFileStorage>();
        services.AddScoped<IImageProcessor, ImageProcessor>();
        services.AddScoped<IFileService, FileService>();

        return services;
    }

    /// <summary>
    /// 添加文件上传功能（使用阿里云 OSS）
    /// </summary>
    public static IServiceCollection AddTenE0FilesWithAliyunOss(this IServiceCollection services, Action<AliyunOssOptions> configure)
    {
        services.Configure(configure);
        services.AddScoped<IFileStorage, AliyunOssStorage>();
        services.AddScoped<IImageProcessor, ImageProcessor>();
        services.AddScoped<IFileService, FileService>();

        return services;
    }

    /// <summary>
    /// 添加文件上传功能（使用 AWS S3）
    /// </summary>
    public static IServiceCollection AddTenE0FilesWithAwsS3(this IServiceCollection services, Action<AwsS3Options> configure)
    {
        services.Configure(configure);
        services.AddScoped<IFileStorage, AwsS3Storage>();
        services.AddScoped<IImageProcessor, ImageProcessor>();
        services.AddScoped<IFileService, FileService>();

        return services;
    }
}
