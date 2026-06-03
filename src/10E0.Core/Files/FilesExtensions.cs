using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenE0.Core.Files;
using TenE0.Core.Files.Storage;

namespace TenE0.Core.DependencyInjection;

/// <summary>
/// 文件上传功能 DI 注册扩展。
///
/// 业务方需要在 DbContext.OnModelCreating 中调用
/// <see cref="FileModelBuilderExtensions.ConfigureTenE0FileAttachmentTables"/>
/// 以注册 <c>TenE0FileAttachment</c> 实体；继承 <c>TenE0SystemDbContext</c> 的 DbContext 已自动完成。
/// </summary>
public static class FilesExtensions
{
    /// <summary>
    /// 添加文件上传功能（使用本地存储）。
    /// TContext 仅需是 DbContext —— 框架表 TenE0FileAttachment 由 TenE0SystemDbContext 自动注册，
    /// 业务方自定义 DbContext 需在 OnModelCreating 调用 ConfigureTenE0FileAttachmentTables。
    /// </summary>
    public static IServiceCollection AddTenE0Files<TContext>(this IServiceCollection services, Action<LocalStorageOptions>? configure = null)
        where TContext : DbContext
    {
        var options = new LocalStorageOptions();
        configure?.Invoke(options);

        services.Configure(configure ?? (opts => { }));
        services.TryAddScoped<IFileStorage, LocalFileStorage>();
        services.TryAddScoped<IImageProcessor, ImageProcessor>();
        services.TryAddScoped<IFileService, FileService<TContext>>();

        return services;
    }

    /// <summary>
    /// 添加文件上传功能（使用阿里云 OSS）。
    /// TContext 同 <see cref="AddTenE0Files{TContext}"/>。
    /// </summary>
    public static IServiceCollection AddTenE0FilesWithAliyunOss<TContext>(this IServiceCollection services, Action<AliyunOssOptions> configure)
        where TContext : DbContext
    {
        services.Configure(configure);
        services.TryAddScoped<IFileStorage, AliyunOssStorage>();
        services.TryAddScoped<IImageProcessor, ImageProcessor>();
        services.TryAddScoped<IFileService, FileService<TContext>>();

        return services;
    }

    /// <summary>
    /// 添加文件上传功能（使用 AWS S3）。
    /// TContext 同 <see cref="AddTenE0Files{TContext}"/>。
    /// </summary>
    public static IServiceCollection AddTenE0FilesWithAwsS3<TContext>(this IServiceCollection services, Action<AwsS3Options> configure)
        where TContext : DbContext
    {
        services.Configure(configure);
        services.TryAddScoped<IFileStorage, AwsS3Storage>();
        services.TryAddScoped<IImageProcessor, ImageProcessor>();
        services.TryAddScoped<IFileService, FileService<TContext>>();

        return services;
    }
}
