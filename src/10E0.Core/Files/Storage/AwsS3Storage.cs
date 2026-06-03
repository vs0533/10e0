using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace TenE0.Core.Files.Storage;

/// <summary>
/// AWS S3 存储实现。
/// <para>
/// 生产环境强烈建议将 <c>AccessKey</c> / <c>SecretKey</c> 配置到环境变量
/// （如 <c>AWS__AccessKey</c> / <c>AWS__SecretKey</c>）、AWS IAM Role（EC2/ECS/EKS
/// 实例角色）、AWS SSO 或 AWS Secrets Manager 中，<b>不要</b>在
/// <c>appsettings.*.json</c> 或源代码中明文写入密钥。本地开发可使用
/// <c>dotnet user-secrets</c> 或 AWS CLI 的 <c>aws configure</c> 凭据链。
/// </para>
/// </summary>
public class AwsS3Storage : IFileStorage
{
    private readonly IAmazonS3 _s3Client;
    private readonly AwsS3Options _options;

    public AwsS3Storage(IOptions<AwsS3Options> options)
    {
        _options = options.Value;
        // 防御性校验：尽早失败，避免 SDK 在后续调用时抛出难以诊断的认证错误。
        AwsS3Options.Validate(_options);
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

/// <summary>
/// AWS S3 配置项。
/// <para>
/// <b>生产环境</b>请通过环境变量（<c>AWS__AccessKey</c> / <c>AWS__SecretKey</c> /
/// <c>AWS__Region</c> / <c>AWS__BucketName</c>）、IAM Role、AWS SSO 或 Secrets Manager 注入，
/// 切勿将真实凭据提交到源代码仓库。
/// </para>
/// </summary>
public class AwsS3Options
{
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";
    public string BucketName { get; set; } = string.Empty;

    /// <summary>
    /// 占位符黑名单（大小写不敏感）。配置值若包含其中任一模式将被视为未配置。
    /// </summary>
    private static readonly string[] PlaceholderPatterns =
    {
        "TODO", "CHANGE_ME", "PLACEHOLDER", "your-"
    };

    /// <summary>
    /// 校验 <see cref="AwsS3Options"/>。任何必填字段为空、或包含占位符模式
    /// （<c>TODO</c> / <c>CHANGE_ME</c> / <c>PLACEHOLDER</c> / <c>your-</c>，大小写不敏感）
    /// 时，抛出 <see cref="OptionsValidationException"/>。
    /// </summary>
    /// <param name="options">待校验的配置实例。</param>
    /// <exception cref="OptionsValidationException">校验失败时抛出。</exception>
    public static void Validate(AwsS3Options options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        EnsureValid(options.AccessKey, nameof(AccessKey), failures);
        EnsureValid(options.SecretKey, nameof(SecretKey), failures);
        EnsureValid(options.Region, nameof(Region), failures);
        EnsureValid(options.BucketName, nameof(BucketName), failures);

        if (failures.Count > 0)
        {
            throw new OptionsValidationException(
                nameof(AwsS3Options),
                typeof(AwsS3Options),
                failures);
        }
    }

    private static void EnsureValid(string value, string fieldName, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add(
                $"AwsS3Options.{fieldName} is required. " +
                $"Configure it via configuration key 'AWS:{fieldName}' " +
                $"(or environment variable 'AWS__{fieldName}'), " +
                $"AWS IAM role, AWS SSO, or `dotnet user-secrets` for local development. " +
                $"Do NOT commit credentials to source control.");
            return;
        }

        foreach (var pattern in PlaceholderPatterns)
        {
            if (value.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(
                    $"AwsS3Options.{fieldName} contains placeholder '{pattern}' " +
                    $"and appears to be unconfigured. " +
                    $"Replace it with a real value sourced from environment variable " +
                    $"'AWS__{fieldName}', AWS IAM role, or AWS Secrets Manager. " +
                    $"Do NOT commit credentials to source control.");
            }
        }
    }
}
