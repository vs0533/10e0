using Aliyun.OSS;
using Microsoft.Extensions.Options;

namespace TenE0.Core.Files.Storage;

/// <summary>
/// 阿里云 OSS 存储实现。
/// <para>
/// 生产环境强烈建议将 <c>AccessKeyId</c> / <c>AccessKeySecret</c> 配置到环境变量
/// （如 <c>OSS__AccessKeyId</c> / <c>OSS__AccessKeySecret</c>）、阿里云 RAM 角色或
/// KMS 凭据提供者中，<b>不要</b>在 <c>appsettings.*.json</c> 或源代码中明文写入密钥。
/// 本地开发可使用 <c>dotnet user-secrets</c>。
/// </para>
/// <para>
/// ⚠️ <b>#99: ThreadPool 占用警告</b> —— <c>Aliyun.OSS</c> SDK 的 <c>OssClient</c>
/// 仅提供同步 API（<c>PutObject</c> / <c>GetObject</c> 等无异步重载），本实现用
/// <c>Task.Run</c> 把同步阻塞 I/O 转移到 ThreadPool。高并发上传/下载场景下每个请求
/// 会占用一个 ThreadPool 线程直到网络往返完成，可能导致 <b>线程池饥饿 → 请求雪崩</b>。
/// 生产部署高吞吐场景应：
/// <list type="bullet">
///   <item>配置 <c>ThreadPool.SetMinThreads</c> ≥ 预期并发上传/下载数，避免按需扩容延迟。</item>
///   <item>或改用 S3 兼容 API（<c>AliyunOssStorage</c> 可替换为基于 AWSSDK / MinIO 的异步实现）。</item>
/// </list>
/// </para>
/// </summary>
public class AliyunOssStorage : IFileStorage
{
    private readonly TimeProvider _timeProvider;
    private readonly OssClient _client;
    private readonly AliyunOssOptions _options;

    public AliyunOssStorage(TimeProvider timeProvider, AliyunOssOptions options)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        _timeProvider = timeProvider;
        _options = options;
        // 防御性校验：尽早失败，避免 SDK 在后续调用时抛出难以诊断的认证错误。
        AliyunOssOptions.Validate(_options);
        _client = new OssClient(_options.Endpoint, _options.AccessKeyId, _options.AccessKeySecret);
    }

    public async Task<StorageResult> StoreAsync(Stream stream, string fileName, string contentType, CancellationToken ct = default)
    {
        var objectKey = GenerateObjectKey(fileName);
        try
        {
            var metadata = new ObjectMetadata { ContentType = contentType };

            await Task.Run(() => _client.PutObject(_options.BucketName, objectKey, stream, metadata), ct);

            var accessUrl = GetAccessUrl(objectKey);
            return new StorageResult(objectKey, accessUrl, true);
        }
        catch (Exception ex)
        {
            // 把已生成的 objectKey 一并返回，方便上层排查（重试/补偿场景可直接定位对象）。
            return new StorageResult(objectKey, string.Empty, false, ex.Message);
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
        var date = _timeProvider.GetUtcNow().UtcDateTime;
        var ext = Path.GetExtension(fileName);
        var uniqueName = $"{Guid.NewGuid()}{ext}";
        return $"{date:yyyy/MM}/{uniqueName}";
    }
}

/// <summary>
/// 阿里云 OSS 配置项。
/// <para>
/// <b>生产环境</b>请通过环境变量（<c>OSS__Endpoint</c> / <c>OSS__AccessKeyId</c> /
/// <c>OSS__AccessKeySecret</c> / <c>OSS__BucketName</c>）、用户密钥或 RAM 角色注入，
/// 切勿将真实凭据提交到源代码仓库。
/// </para>
/// </summary>
public class AliyunOssOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string AccessKeyId { get; set; } = string.Empty;
    public string AccessKeySecret { get; set; } = string.Empty;
    public string BucketName { get; set; } = string.Empty;

    /// <summary>
    /// 占位符黑名单（大小写不敏感）。配置值若包含其中任一模式将被视为未配置。
    /// </summary>
    private static readonly string[] PlaceholderPatterns =
    {
        "TODO", "CHANGE_ME", "PLACEHOLDER", "your-"
    };

    /// <summary>
    /// 校验 <see cref="AliyunOssOptions"/>。任何必填字段为空、或包含占位符模式
    /// （<c>TODO</c> / <c>CHANGE_ME</c> / <c>PLACEHOLDER</c> / <c>your-</c>，大小写不敏感）
    /// 时，抛出 <see cref="OptionsValidationException"/>。
    /// </summary>
    /// <param name="options">待校验的配置实例。</param>
    /// <exception cref="OptionsValidationException">校验失败时抛出。</exception>
    public static void Validate(AliyunOssOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        EnsureValid(options.Endpoint, nameof(Endpoint), failures);
        EnsureValid(options.AccessKeyId, nameof(AccessKeyId), failures);
        EnsureValid(options.AccessKeySecret, nameof(AccessKeySecret), failures);
        EnsureValid(options.BucketName, nameof(BucketName), failures);

        if (failures.Count > 0)
        {
            throw new OptionsValidationException(
                nameof(AliyunOssOptions),
                typeof(AliyunOssOptions),
                failures);
        }
    }

    private static void EnsureValid(string value, string fieldName, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add(
                $"AliyunOssOptions.{fieldName} is required. " +
                $"Configure it via configuration key 'OSS:{fieldName}' " +
                $"(or environment variable 'OSS__{fieldName}'), " +
                $"Aliyun RAM role, or `dotnet user-secrets` for local development. " +
                $"Do NOT commit credentials to source control.");
            return;
        }

        foreach (var pattern in PlaceholderPatterns)
        {
            if (value.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(
                    $"AliyunOssOptions.{fieldName} contains placeholder '{pattern}' " +
                    $"and appears to be unconfigured. " +
                    $"Replace it with a real value sourced from environment variable " +
                    $"'OSS__{fieldName}' or an Aliyun RAM role. " +
                    $"Do NOT commit credentials to source control.");
            }
        }
    }
}
