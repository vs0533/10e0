using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace TenE0.Core.Files.Storage;

/// <summary>
/// AWS S3 存储实现
/// </summary>
public class AwsS3Storage : IFileStorage
{
    private readonly IAmazonS3 _s3Client;
    private readonly AwsS3Options _options;

    public AwsS3Storage(IOptions<AwsS3Options> options)
    {
        _options = options.Value;
        _s3Client = new AmazonS3Client(_options.AccessKey, _options.SecretKey, Amazon.RegionEndpoint.GetBySystemName(_options.Region));
    }

    public async Task<StorageResult> StoreAsync(Stream stream, string fileName, string contentType, CancellationToken ct = default)
    {
        try
        {
            var key = GenerateObjectKey(fileName);
            var request = new PutObjectRequest
            {
                BucketName = _options.BucketName,
                Key = key,
                InputStream = stream,
                ContentType = contentType
            };

            await _s3Client.PutObjectAsync(request, ct);

            var accessUrl = GetAccessUrl(key);
            return new StorageResult(key, accessUrl, true);
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
            var request = new GetObjectRequest
            {
                BucketName = _options.BucketName,
                Key = storagePath
            };

            var response = await _s3Client.GetObjectAsync(request, ct);
            var ms = new MemoryStream();
            await response.ResponseStream.CopyToAsync(ms, ct);
            ms.Position = 0;
            return ms;
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
            var request = new DeleteObjectRequest
            {
                BucketName = _options.BucketName,
                Key = storagePath
            };

            await _s3Client.DeleteObjectAsync(request, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public string GetAccessUrl(string storagePath)
    {
        return $"https://{_options.BucketName}.s3.{_options.Region}.amazonaws.com/{storagePath}";
    }

    public async Task<bool> ExistsAsync(string storagePath, CancellationToken ct = default)
    {
        try
        {
            var request = new GetObjectMetadataRequest
            {
                BucketName = _options.BucketName,
                Key = storagePath
            };

            await _s3Client.GetObjectMetadataAsync(request, ct);
            return true;
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

public class AwsS3Options
{
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";
    public string BucketName { get; set; } = string.Empty;
}
