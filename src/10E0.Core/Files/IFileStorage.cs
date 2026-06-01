namespace TenE0.Core.Files;

/// <summary>
/// 文件存储抽象层
/// </summary>
public interface IFileStorage
{
    /// <summary>
    /// 存储文件
    /// </summary>
    Task<StorageResult> StoreAsync(Stream stream, string fileName, string contentType, CancellationToken ct = default);

    /// <summary>
    /// 读取文件
    /// </summary>
    Task<Stream?> RetrieveAsync(string storagePath, CancellationToken ct = default);

    /// <summary>
    /// 删除文件
    /// </summary>
    Task<bool> DeleteAsync(string storagePath, CancellationToken ct = default);

    /// <summary>
    /// 获取文件访问 URL
    /// </summary>
    string GetAccessUrl(string storagePath);

    /// <summary>
    /// 检查文件是否存在
    /// </summary>
    Task<bool> ExistsAsync(string storagePath, CancellationToken ct = default);
}

public record StorageResult(
    string StoragePath,
    string AccessUrl,
    bool Success,
    string? ErrorMessage = null
);
