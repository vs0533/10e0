using Aliyun.OSS;
using Microsoft.Extensions.Options;

namespace TenE0.Core.Files.Storage;

/// <summary>
/// 阿里云 OSS 存储实现
/// </summary>
public class AliyunOssStorage : IFileStorage
{
    private readonly OssClient _client;
    private readonly AliyunOssOptions _options;

    public AliyunOssStorage(IOptions<AliyunOssOptions> options)
    {
        _options = options.Value;
        _client = new OssClient(_options.Endpoint, _options.AccessKeyId, _options.AccessKeySecret);
    }

    public async Task<StorageResult> StoreAsync(Stream stream, string fileName, string contentType, CancellationToken ct = default)
    {
        try
        {
            var objectKey = GenerateObjectKey(fileName);
            var metadata = new ObjectMetadata { ContentType = contentType };

            await Task.Run(() => _client.PutObject(_options.BucketName, objectKey, stream, metadata), ct);

            var accessUrl = GetAccessUrl(objectKey);
            return new StorageResult(objectKey, accessUrl, true);
        }
        catch (Exception ex)
        {
            return new StorageResult(string.Empty, string.Empty, false, ex.Message);
        }
    }

    public async Task<Stream?> RetrieveAsync(string storagePath, CancellationToken ct = default)
    {
        try
        {
            var result = await Task.Run(() => _client.GetObject(_options.BucketName, storagePath), ct);
            return result.Content;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> DeleteAsync(string storagePath, CancellationToken ct = default)
    {
        try
        {
            await Task.Run(() => _client.DeleteObject(_options.BucketName, storagePath), ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public string GetAccessUrl(string storagePath)
    {
        return $"https://{_options.BucketName}.{_options.Endpoint}/{storagePath}";
    }

    public async Task<bool> ExistsAsync(string storagePath, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() => _client.DoesObjectExist(_options.BucketName, storagePath), ct);
        }
        catch
        {
            return false;
        }
    }

    private string GenerateObjectKey(string fileName)
    {
        var date = DateTime.Now;
        var ext = Path.GetExtension(fileName);
        var uniqueName = $"{Guid.NewGuid()}{ext}";
        return $"{date:yyyy/MM}/{uniqueName}";
    }
}

public class AliyunOssOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string AccessKeyId { get; set; } = string.Empty;
    public string AccessKeySecret { get; set; } = string.Empty;
    public string BucketName { get; set; } = string.Empty;
}
