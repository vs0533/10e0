using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace TenE0.Core.Files;

/// <summary>
/// 文件服务实现。
///
/// 泛型化设计：TContext 仅需是 <see cref="DbContext"/>，实体 <c>TenE0FileAttachment</c>
/// 通过 <see cref="Storage.FileModelBuilderExtensions.ConfigureTenE0FileAttachmentTables"/>
/// 在 DbContext 的 OnModelCreating 中注册即可。
/// </summary>
public class FileService<TContext>(
    IDbContextFactory<TContext> contextFactory,
    IFileStorage storage,
    IImageProcessor imageProcessor,
    ILogger<FileService<TContext>> logger) : IFileService
    where TContext : DbContext
{
    public async Task<UploadResponse> UploadAsync(Stream stream, string fileName, string contentType, UploadRequest? request = null, CancellationToken ct = default)
    {
        request ??= new UploadRequest();

        // 存储文件
        var storageResult = await storage.StoreAsync(stream, fileName, contentType, ct);
        if (!storageResult.Success)
        {
            throw new InvalidOperationException($"文件存储失败: {storageResult.ErrorMessage}");
        }

        // 计算文件哈希
        stream.Position = 0;
        var hash = await ComputeHashAsync(stream, ct);

        // 获取图片尺寸（如果是图片）
        int? width = null, height = null;
        if (contentType.StartsWith("image/"))
        {
            stream.Position = 0;
            var dims = await imageProcessor.GetDimensionsAsync(stream, ct);
            width = dims.Width;
            height = dims.Height;
        }

        // 保存元数据
        var attachment = new TenE0FileAttachment
        {
            FileName = fileName,
            StoragePath = storageResult.StoragePath,
            ContentType = contentType,
            FileSize = stream.Length,
            StorageBackend = request.Backend.ToString(),
            Category = request.Category,
            FileHash = hash,
            Width = width,
            Height = height,
            RelatedEntityId = request.RelatedEntityId,
            RelatedEntityType = request.RelatedEntityType
        };

        await using var context = await contextFactory.CreateDbContextAsync(ct);
        context.Set<TenE0FileAttachment>().Add(attachment);
        await context.SaveChangesAsync(ct);

        return new UploadResponse(
            attachment.Id,
            fileName,
            storageResult.StoragePath,
            storageResult.AccessUrl,
            attachment.FileSize,
            contentType
        );
    }

    public async Task<UploadResponse> UploadImageAsync(Stream stream, string fileName, ImageProcessOptions? options = null, UploadRequest? request = null, CancellationToken ct = default)
    {
        if (options != null)
        {
            // 处理图片
            var processResult = await imageProcessor.ProcessAsync(stream, options, ct);
            if (!processResult.Success)
            {
                throw new InvalidOperationException($"图片处理失败: {processResult.ErrorMessage}");
            }

            // 上传处理后的图片
            var response = await UploadAsync(processResult.ProcessedStream, fileName, "image/jpeg", request, ct);

            // 生成缩略图（如果需要）
            if (options.GenerateThumbnail)
            {
                stream.Position = 0;
                var thumbnailStream = await imageProcessor.GenerateThumbnailAsync(stream, options.ThumbnailWidth, options.ThumbnailHeight, ct);
                // 缩略图文件名：原名 + "_thumb" 后缀（避免 thumb.jpg → thumb_thumb.jpg 的前缀重复，
                // 也让缩略图与原图在文件列表中相邻排序）。
                var thumbnailName = $"{Path.GetFileNameWithoutExtension(fileName)}_thumb{Path.GetExtension(fileName)}";
                var thumbnailResult = await storage.StoreAsync(thumbnailStream, thumbnailName, "image/jpeg", ct);

                if (thumbnailResult.Success)
                {
                    await using var context = await contextFactory.CreateDbContextAsync(ct);
                    var attachment = await context.Set<TenE0FileAttachment>().FindAsync(new object[] { response.Id }, ct);
                    if (attachment != null)
                    {
                        attachment.ThumbnailPath = thumbnailResult.StoragePath;
                        await context.SaveChangesAsync(ct);
                    }
                }
            }

            return response;
        }

        // 无处理选项，直接上传
        return await UploadAsync(stream, fileName, "image/jpeg", request, ct);
    }

    public async Task<(Stream? Stream, TenE0FileAttachment? Metadata)> DownloadAsync(string fileId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var attachment = await context.Set<TenE0FileAttachment>().FindAsync(new object[] { fileId }, ct);
        if (attachment == null || attachment.IsDeleted)
        {
            return (null, null);
        }

        var stream = await storage.RetrieveAsync(attachment.StoragePath, ct);
        return (stream, attachment);
    }

    public async Task<DeleteResult> DeleteAsync(string fileId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var attachment = await context.Set<TenE0FileAttachment>().FindAsync(new object[] { fileId }, ct);
        if (attachment == null || attachment.IsDeleted)
        {
            // 文件不存在或已软删除：两个维度都视为"未删除"，无副作用
            return new DeleteResult(MetadataDeleted: false, StorageDeleted: false);
        }

        // 软删除元数据（无论 storage 是否成功，元数据都要置 IsDeleted=true，
        // 否则 DownloadAsync/GetMetadataAsync 还会返回这条记录 → 业务方拿到的 URL
        // 指向一个已请求删除的对象，造成"按 ID 查得到但下载 404"的诡异体验）。
        attachment.IsDeleted = true;
        await context.SaveChangesAsync(ct);

        // 物理文件删除：主文件 + 缩略图，任一失败就置 StorageDeleted=false
        var storageDeleted = true;
        var mainDeleted = await storage.DeleteAsync(attachment.StoragePath, ct);
        if (!mainDeleted)
        {
            logger.LogWarning("文件存储删除失败: {StoragePath}", attachment.StoragePath);
            storageDeleted = false;
        }

        if (!string.IsNullOrEmpty(attachment.ThumbnailPath))
        {
            var thumbDeleted = await storage.DeleteAsync(attachment.ThumbnailPath, ct);
            if (!thumbDeleted)
            {
                logger.LogWarning("缩略图存储删除失败: {ThumbnailPath}", attachment.ThumbnailPath);
                storageDeleted = false;
            }
        }

        return new DeleteResult(MetadataDeleted: true, StorageDeleted: storageDeleted);
    }

    public async Task<TenE0FileAttachment?> GetMetadataAsync(string fileId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        return await context.Set<TenE0FileAttachment>().FirstOrDefaultAsync(a => a.Id == fileId && !a.IsDeleted, ct);
    }

    public async Task<string?> GetAccessUrlAsync(string fileId, CancellationToken ct = default)
    {
        var attachment = await GetMetadataAsync(fileId, ct);
        if (attachment == null) return null;

        return storage.GetAccessUrl(attachment.StoragePath);
    }

    private static async Task<string> ComputeHashAsync(Stream stream, CancellationToken ct = default)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = await sha256.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hashBytes);
    }
}
