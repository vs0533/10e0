using TenE0.Core.Files.Storage;

namespace TenE0.Core.Files;

/// <summary>
/// 文件服务接口
/// </summary>
public interface IFileService
{
    /// <summary>
    /// 上传文件
    /// </summary>
    Task<UploadResponse> UploadAsync(Stream stream, string fileName, string contentType, UploadRequest? request = null, CancellationToken ct = default);

    /// <summary>
    /// 上传图片（带处理选项）
    /// </summary>
    Task<UploadResponse> UploadImageAsync(Stream stream, string fileName, ImageProcessOptions? options = null, UploadRequest? request = null, CancellationToken ct = default);

    /// <summary>
    /// 下载文件
    /// </summary>
    Task<(Stream? Stream, TenE0FileAttachment? Metadata)> DownloadAsync(string fileId, CancellationToken ct = default);

    /// <summary>
    /// 删除文件
    /// </summary>
    Task<bool> DeleteAsync(string fileId, CancellationToken ct = default);

    /// <summary>
    /// 获取文件元数据
    /// </summary>
    Task<TenE0FileAttachment?> GetMetadataAsync(string fileId, CancellationToken ct = default);

    /// <summary>
    /// 获取文件的访问 URL
    /// </summary>
    Task<string?> GetAccessUrlAsync(string fileId, CancellationToken ct = default);
}
